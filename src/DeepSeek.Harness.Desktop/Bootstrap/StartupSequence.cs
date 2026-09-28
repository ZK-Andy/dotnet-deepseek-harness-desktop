using Microsoft.Extensions.DependencyInjection;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动编排服务（ADR compose-root-form-separation）：阶段主链与用例编排自组合根整体搬迁至此
/// （语句序/分支/异常边界逐一对应，纯搬家零行为变更）；组合根只装配与触发（R1）。Ryn 无 hosted-service
/// 机制，编排器由组合根 Build 后经 <see cref="IStartupSequence"/> 显式触发（结构必然，见 ADR Alternatives）。
/// 长命惰性接线槽在 <see cref="StartupWiring"/>；阶段产出为正式类型（<see cref="Preflight"/> 等）。
/// 本文件承载启动主链与产生段；监督/退出接线在 <c>StartupSequence.Supervision.cs</c>，
/// 导航原语与窗口就绪等待在 <c>StartupSequence.Navigation.cs</c>，网页会话自愈在 <c>StartupSequence.WebSession.cs</c>。
/// </summary>
internal sealed partial class StartupSequence : IStartupSequence
{
    private readonly Preflight _preflight;
    private readonly AppSetup _app;
    private readonly UpdateSetup _updates;
    private readonly UiLocale _uiLocale;
    private readonly RuntimeTimeouts _timeouts;
    private readonly DshShellForward _shellForward;
    private readonly DshLoopbackProxy? _proxy;
    private readonly StartupWiring _wiring;
    private readonly PrimaryListener? _instanceListener;

    /// <summary>创建编排服务（构造数据注入；由组合根在 Build 后经容器工厂解析）。</summary>
    public StartupSequence(
        Preflight preflight,
        AppSetup app,
        UpdateSetup updates,
        UiLocale uiLocale,
        RuntimeTimeouts timeouts,
        DshShellForward shellForward,
        DshLoopbackProxy? proxy,
        StartupWiring wiring,
        PrimaryListener? instanceListener)
    {
        _preflight = preflight;
        _app = app;
        _updates = updates;
        _uiLocale = uiLocale;
        _timeouts = timeouts;
        _shellForward = shellForward;
        _proxy = proxy;
        _wiring = wiring;
        _instanceListener = instanceListener;
    }

    /// <summary>组合根入口：按原 <c>Program.Main</c> 语句序执行全部编排并返回进程退出码。</summary>
    public int Run()
    {
        try
        {
            EnsureDesktopProfile();
            HostSetup host = SetupHostAndMarker();
            InstallCompanionBeforeSpawn(host);
            StartRuntime(host);
            RunBootstrapIfNeeded();
            ShowTray();
            SupervisorSetup supervisor = SetupSupervisor(host);
            SetupHealthMonitor(supervisor);
            StartUpdateCheck();
            SharedHomeBannerTask(host, supervisor);
            return RunAppLoop(supervisor);
        }
        finally
        {
            // 原 `using var host` / `using var supervisorCts` 作用域到 Run 末尾；编排搬迁后释放随编排走。
            // 代理资源不在此——代理在容器之前启动，释放留在组合根 Run 的 finally（ProxySetup.Dispose）。
            _wiring.Host?.Dispose();
            _wiring.SupervisorCts?.Dispose();
        }
    }

    private void EnsureDesktopProfile()
    {
        // 桌面专属 profile 前置（ADR shared-home-desktop-profile / desktop-profile-rename）：
        // 先迁移旧名目录（上游 0.1.5-alpha.1 起 CLI 圈占字面名 desktop），再自举——上游对自定义 profile 名
        // 不自动初始化，缺清单直接拒启；必须在 spawn 前确保 profile 就绪（幂等，已存在则零写入）。
        try
        {
            DesktopProfileBootstrap.MigrateLegacyProfileName(HarnessRuntimeHost.ResolveDshHome(), HostLog.Write);

            // 事务管线 recover（ADR transactional-plugin-pipeline）：上轮插件事务被中断时按 journal
            // 重放/回滚，并清扫 stray staging/rollback 目录。必须在 EnsureProfile/探针/spawn 之前——
            // journal 损坏在此 fail loud（异常进入下方 catch 记日志，dsh 起不来的后果由降级链路兜底）。
            PluginProfileTransaction.Recover(HarnessRuntimeHost.ResolveDshHome(), HostLog.Write);

            if (DesktopProfileBootstrap.EnsureProfile(HarnessRuntimeHost.ResolveDshHome()))
            {
                HostLog.Write($"[host] 已初始化 profiles/{HarnessRuntimeHost.DesktopProfileName}（bundles 对齐 web 模板）");
            }

            // 启动前 reconcile 不可解析的 bundle 引用（ADR online-first-unbundled-runtime 批次三，
            // 对齐 dsh-tauri-desk #177：退役随包种子后，存量 profile 可能残留指向已消失 tgz 的
            // file:/link: 引用，dsh 启动时视作不可解析 → 卡死循环）。必须在 spawn 前清理。
            int reconciled = DesktopProfileBootstrap.ReconcileProfile(HarnessRuntimeHost.ResolveDshHome(), HostLog.Write);
            if (reconciled > 0)
            {
                HostLog.Write($"[host] 桌面 profile reconcile：移除 {reconciled} 个不可解析插件引用");
            }
        }
        catch (Exception ex)
        {
            HostLog.Write($"[host] profiles/{HarnessRuntimeHost.DesktopProfileName} 初始化失败（dsh 可能拒启，详见后续降级链路）：{ex.Message}");
        }
    }

    /// <summary>宿主与崩溃标记创建（阶段产出）。</summary>
    private HostSetup SetupHostAndMarker()
    {
        // 原 `using var host`：生命周期由 Run 的 finally 释放（接线 wiring.Host，引导服务惰性读）。
        // 全局 dsh 模型：宿主恒以 PATH dsh（bundled=null）形态运行（ADR simple-shell-single-global-dsh）。
        HarnessRuntimeHost host = new(HostLog.Write);
        _wiring.Host = host;

        // 崩溃取证 marker（ADR shell-observability-diagnostics）：遗留即判定上轮非受控退出；
        // 正常退出路径在退出管道清除
        RunMarkerResult marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());
        if (marker.PreviousRunUnclean)
        {
            HostLog.Write("[host] 检测到上轮未正常退出的标记；如频繁出现请在设置页导出诊断信息");
        }

        return new HostSetup(host, marker);
    }

    private void InstallCompanionBeforeSpawn(HostSetup host)
    {
        // host 是顺序契约参数：随包插件安装必须发生在宿主已建、dsh spawn 之前（原 HostToken 的偏序承诺）。
        // 对齐参照（dsh-tauri-desk launch.rs）：随包插件（companion）在 spawn dsh 前安装，绝不
        // 「启动后 3s 装 → 重启」。全局 dsh 模型（ADR simple-shell-single-global-dsh）：dsh 在 PATH 上，
        // nodeExe/dshEntry 传 null，EnsureBundledPluginsBeforeSpawnAsync 内回退到 PATH 上的 dsh 命令。
        // dev 显式覆盖共享 home 时跳过（防串扰）。
        if (!_preflight.Bootstrap.IsNeeded && !(_preflight.Launch.IsDev && !_preflight.Launch.DevAutoIsolated))
        {
            bool installed = false;
            try
            {
                installed = MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                    nodeExe: null,
                    dshEntry: null,
                    HarnessRuntimeHost.ResolveDshHome(),
                    Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                    HostLog.Write,
                    PluginProcessRunner.RunAsync,
                    PluginProcessRunner.RunProbeAsync,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                HostLog.Write($"[host] 随包插件 spawn 前安装失败（跳过，不阻断启动）：{ex.Message}");
            }

            if (installed)
            {
                // 事务管线（ADR transactional-plugin-pipeline）：staged 体检在换入前已过，active 即新完整态
                HostLog.Write("[host] 随包插件经事务管线换入 active（staged 体检已过）");
            }
        }
    }

    private void StartRuntime(HostSetup host)
    {
        // CLI shim 注册（ADR simple-shell-single-global-dsh）：dsh 已全局在 PATH，仅注册 pnpm shim。
        // best-effort——注册内部吞预期异常（见 CliShimRegistrar），此处再兜底意外异常。
        _preflight.Bootstrap.RegisterCliShim();

        DshWebUrl? webUrl = _preflight.Bootstrap.IsNeeded
            ? null
            : DshWebUrl.FromNullable(host.Host.StartAsync(timeout: TimeSpan.FromSeconds(_timeouts.SpawnTimeoutSeconds)).GetAwaiter().GetResult());
        if (!_preflight.Bootstrap.IsNeeded)
        {
            HostLog.Write($"[host] runtime = {host.Host.RuntimeDescription}");
            if (webUrl is not null)
            {
                HostLog.Write($"[host] dsh web = {webUrl}");
            }
            else
            {
                HostLog.Write($"[host] dsh 未在时限内给出 URL；降级加载 wwwroot。stderr 尾巴：\n{string.Join('\n', host.Host.StderrTail.TakeLast(8))}");
            }
        }

        if (webUrl is not null)
        {
            // 壳铸币（对齐上游 authenticateWebHost）：窗口/导航一律走壳 origin，先铸后载；
            // 失败 loud，窗口照开（转发 401/502 → 探针/恢复面按错误页处理，不挡启动）。
            // 同步编排沿用既有 GetAwaiter 形态；上限由转发器内部超时兜底。
            _ = _shellForward.MintAsync(webUrl.Value, HostLog.Write, CancellationToken.None).GetAwaiter().GetResult();
        }

        // dsh web URL 就此收官：下游（监督器/收养导航）各自从宿主或回调取得 URL，
        // 无跨阶段消费点——不设阶段产出（死载荷会被 CompositionRootSequenceTests 拦下）。
    }

    private void RunBootstrapIfNeeded()
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），引导服务后台完成
        // 检测/下载/安装/验证后起 dsh，并把就位 URL 交回调接回壳侧导航；失败推错误态等待用户重试
        // （desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控——依赖序即插入位。
        _preflight.Bootstrap.Start((url, ct) => EnterMainUiAsync(url, ct));
    }

    /// <summary>托盘就绪化（装配壳）：解析 Ryn 托盘服务并交给控制器（无托盘环境传 null）。</summary>
    private void ShowTray()
    {
        // 托盘在组合根装配期构造（wiring.Tray），ShowTray 必然晚于它
        Tray.TrayController tray = _wiring.Tray!;
        tray.Show(tray.IsAvailable
            ? () => _app.App.Services.GetRequiredService<TrayService>()
            : null);
    }

    private void StartUpdateCheck()
    {
        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        _updates.Updates.Start();
    }

    private int RunAppLoop(SupervisorSetup supervisor)
    {
        HostLog.Write("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            _app.App.Run();
        }
        catch (Exception ex)
        {
            HostLog.Write($"[host] Ryn Run 异常：{ex}");
        }

        HostLog.Write("[host] Ryn Run 结束");
        supervisor.Cts.Cancel();
        _preflight.Bootstrap.Cancel();
        try
        {
            supervisor.Task.Wait(TimeSpan.FromSeconds(_timeouts.SupervisorJoinTimeoutSeconds));
        }
        catch (AggregateException)
        {
            // 监督任务随宿主回收而结束；无需上报
        }

        // 非 orderly 退出路径（用户直接关窗使 Run 返回）与托盘有序退出共用同一单实例管道：
        // once-guard 使已回收路径的重复调用为 no-op（退出管道在 SetupSupervisor 的 WireExitHandlers 接线）
        _wiring.Exit!.ReapRuntime();
        // 非 orderly 退出路径也要释放单实例锁地址：orderly 路径已 Dispose 过，幂等守卫保证此处安全
        _instanceListener?.Dispose();
        return 0;
    }
}
