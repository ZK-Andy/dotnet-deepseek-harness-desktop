using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 桌面壳组合根（ADR split-program-main-god-function）：承载原 <c>Program.Main</c> 的全部编排。
/// 纯抽取、零行为变更——语句顺序/分支/异常边界与原 Main 逐一对应；共享状态为字段（_camelCase）、
/// 编排方法按关注面拆为 partial（App/Lifecycle），本文件承载核心入口与装配前奏。
/// </summary>
public sealed partial class DesktopBootstrap
{
    // —— 共享状态（组合根只保留顺序资源与服务引用；域状态已下沉真类型服务）——
    // 批次 2 值流后保留字段的用途只有两类，其余状态一律经阶段产出参数流动：
    //   1) 早于赋值的惰性捕获点——_host/_app/_windowAccessor 在服务/控制器构造期就被 () => 委托读取，
    //      赋值时点晚于构造点，故必须留在字段（参数不可能先于自身构造流入）；
    //   2) 赋值后仍被异步回调改写/读取或跨阶段延迟接线——_webUrl（导航靶点）/_supervisorCtsRef（退出令牌）/
    //      _exit（托盘与自更新兜底路由）/_healthMonitor（诊断快照）/_tray（二次启动回调）/锁定资源。
    // _supervisorCtsRef：命令路由在 BuildApp 的 RegisterServices 注册时点早于 SetupSupervisor 赋值，
    // 故先持一个可空引用、由 SetupSupervisor 接线，路由经 backgroundToken 闭包惰性读它——服务注册期
    // 需要的是延迟捕获；Run 尾部也用它释放（唯一 CTS 持有字段，勿再引入别名）。
    private CancellationTokenSource? _supervisorCtsRef;
    private UiLocale _uiLocale = null!;
    private PrimaryListener? _instanceListener;
    private Services.Tray.TrayController _tray = null!;
    private Services.PageHealthMonitor? _healthMonitor;
    private RynApplication _app = null!;
    private CurrentWindowAccessor _windowAccessor = null!;
    private Uri? _webUrl;
    private HarnessRuntimeHost _host = null!;
    private Core.ExitPipeline _exit = null!;

    // —— 启动编排阶段产出（ADR composition-root-value-flow-pipeline 批次 2）——
    // 阶段方法返回真实产出（上一形态的空载荷 token 已删除），消费段收参数：产出的值本身就是
    // 执行序证明——绕过产生阶段即缺值编译失败（如没有 StartRuntime 的 RuntimeSetup 就拿不到
    // webUrl）。值类型归属按拍板 2：跨 R3 边界的 DshWebUrl 进 Core；只在本编排器内流通的阶段
    // 产出留私有嵌套（不构成根命名空间独立类型）。值的寿命 = 单段——长命共享态仍留字段/服务，
    // 不借阶段返回值回填全局态。
    private readonly record struct Preflight(IFirstBootBootstrap Bootstrap, bool IsDev, bool DevAutoIsolated);
    private readonly record struct HostSetup(HarnessRuntimeHost Host, RunMarkerResult Marker);
    private readonly record struct RuntimeSetup(DshWebUrl? WebUrl);
    private readonly record struct UpdateSetup(Services.Update.UpdateCoordinator Updates);
    private readonly record struct AppSetup(RynApplication App, CurrentWindowAccessor WindowAccessor);
    private readonly record struct SupervisorSetup(CancellationTokenSource Cts, Task Task);

    /// <summary>组合根入口：按原 <c>Program.Main</c> 语句序执行全部编排并返回进程退出码。</summary>
    public int Run()
    {
        Preflight preflight = ResolveRuntimeAndDev();
        if (!AcquireSingleInstance(preflight))
        {
            return 0;
        }

        EnsureDesktopProfile();
        try
        {
            HostSetup host = SetupHostAndMarker(preflight);
            InstallCompanionBeforeSpawn(preflight, host);
            RuntimeSetup runtime = StartRuntime(preflight, host);
            UpdateSetup update = InitCloseGateAndUpdateStack(preflight, runtime);
            AppSetup app = BuildApp(preflight, runtime, update);
            RunBootstrapIfNeeded(preflight, app);
            ShowTray(app);
            SupervisorSetup supervisor = SetupSupervisor(preflight, app, host);
            SetupHealthMonitor(app, supervisor);
            StartUpdateCheck(update);
            SharedHomeBannerTask(preflight, app, host, supervisor);
            return RunAppLoop(preflight, app, supervisor);
        }
        finally
        {
            // 原 `using var host` / `using var supervisorCts` 作用域到 Main 末尾；这里在 Run 末尾等价释放。
            _host?.Dispose();
            _supervisorCtsRef?.Dispose();
        }
    }

    private Preflight ResolveRuntimeAndDev()
    {
        // 首启引导服务（R3 端口实现，ADR composition-root-value-flow-pipeline 批次 1）：全局 node/dsh
        // 引导、插件装配、CLI shim、宿主启动从组合根下沉；页面反馈经 FirstBootUi 注入，宿主惰性提供。
        IFirstBootBootstrap bootstrap = new FirstBootBootstrapService(
            () => _host,
            new Services.FirstBootUi(() => _windowAccessor),
            HostLog.Write);
        bootstrap.Resolve();

        string? devRuntimeDir = Environment.GetEnvironmentVariable(DevEnvironment.RuntimeDirEnv);
        string? devFlag = Environment.GetEnvironmentVariable(DevEnvironment.DevFlagEnv);
        bool isDev = DevEnvironment.IsDevRuntime(devRuntimeDir, devFlag);
        bool devAutoIsolated = false;
        if (isDev && Environment.GetEnvironmentVariable(DevEnvironment.HomeOverrideEnv) is null)
        {
            string? devHome = DevEnvironment.DeriveDefaultDevHome(devRuntimeDir, AppContext.BaseDirectory);
            if (devHome is not null)
            {
                Environment.SetEnvironmentVariable(DevEnvironment.HomeOverrideEnv, devHome);
                devAutoIsolated = true;
                HostLog.Write($"[host] 开发运行时：DSH_HOME 隔离到 {devHome}；ApplicationId 带 .dev 后缀，可与正式版并存");
            }
        }
        else if (!isDev &&
                 DevEnvironment.DeriveDefaultDevHome(null, AppContext.BaseDirectory) is not null)
        {
            // dev 判定改显式标记后的唯一残留风险（R2 评审）：贡献者在仓库内跑却忘带
            // DSH_DESKTOP_DEV=1 —— 判定按设计走打包产品语义，但值得一条 host.log 诊断指路
            HostLog.Write("[host] 疑似仓库内开发运行但未设 DSH_DESKTOP_DEV=1：按打包产品处理（共享真实 home，无 dev 隔离）");
        }

        return new Preflight(bootstrap, isDev, devAutoIsolated);
    }

    /// <summary>单实例仲裁（ADR single-instance-launcher-activation）：false = 已有主实例，调用方直接返回 0。
    /// 依赖 <paramref name="preflight"/> 产出的 dev 判定（单实例 socket 按 dev/正式分域）。</summary>
    private bool AcquireSingleInstance(Preflight preflight)
    {
        // 宿主 UI 语言单点（ADR host-ui-locale）：companion 上报 dsh locale，托盘/横幅据此出双语
        _uiLocale = new UiLocale();
        string? xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? instanceSocketPath = OperatingSystem.IsWindows()
            ? null // Windows 无验证环境不启用互斥，行为维持现状（ADR 平台边界）
            : LauncherActivation.SocketPath(
                xdgRuntimeDir is { Length: > 0 } ? xdgRuntimeDir : Path.GetTempPath(),
                "deepseek-harness-desktop" +
                (xdgRuntimeDir is { Length: > 0 } ? string.Empty : LauncherActivation.FallbackUidSuffix()),
                preflight.IsDev);
        if (instanceSocketPath is not null)
        {
            if (!LauncherActivation.TryBindPrimary(
                    instanceSocketPath,
                    onShowRequested: async () =>
                    {
                        // 托盘控制器在 InitCloseGateAndUpdateStack 构造：启动极早期到达的激活请求
                        // 静默忽略（控制器与窗口均未就绪）
                        if (_tray is null)
                        {
                            return;
                        }

                        await _tray.ActivateFromLauncherAsync();
                    },
                    HostLog.Write,
                    out _instanceListener))
            {
                bool notified = LauncherActivation.NotifyPrimary(instanceSocketPath, TimeSpan.FromSeconds(2));
                HostLog.Write(
                    $"[host] 已有主实例在运行（launcher 二次启动）：通知显示主窗{(notified ? "成功" : "未达（主实例可能正忙）")}，本次启动退出");
                return false;
            }
        }

        return true;
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
    /// <param name="preflight">顺序契约参数：<see cref="HarnessRuntimeHost.ResolveDshHome"/> 读的
    /// DSH_HOME 由 ResolveRuntimeAndDev 的 dev 隔离设置，宿主创建必须先于其消费。</param>
    private HostSetup SetupHostAndMarker(Preflight preflight)
    {
        // 原 `using var host`：生命周期由 Run 的 finally 释放（本方法赋值 _host）。
        // 全局 dsh 模型：宿主恒以 PATH dsh（bundled=null）形态运行（ADR simple-shell-single-global-dsh）。
        _host = new HarnessRuntimeHost(HostLog.Write);

        // 崩溃取证 marker（ADR shell-observability-diagnostics）：遗留即判定上轮非受控退出；
        // 正常退出路径在退出管道清除
        RunMarkerResult marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());
        if (marker.PreviousRunUnclean)
        {
            HostLog.Write("[host] 检测到上轮未正常退出的标记；如频繁出现请在设置页导出诊断信息");
        }

        return new HostSetup(_host, marker);
    }

    private void InstallCompanionBeforeSpawn(Preflight preflight, HostSetup host)
    {
        // host 是顺序契约参数：随包插件安装必须发生在宿主已建、dsh spawn 之前（原 HostToken 的偏序承诺）。
        // 对齐参照（dsh-tauri-desk launch.rs）：随包插件（companion）在 spawn dsh 前安装，绝不
        // 「启动后 3s 装 → 重启」。全局 dsh 模型（ADR simple-shell-single-global-dsh）：dsh 在 PATH 上，
        // nodeExe/dshEntry 传 null，EnsureBundledPluginsBeforeSpawnAsync 内回退到 PATH 上的 dsh 命令。
        // dev 显式覆盖共享 home 时跳过（防串扰）。
        if (!preflight.Bootstrap.IsNeeded && !(preflight.IsDev && !preflight.DevAutoIsolated))
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

    private RuntimeSetup StartRuntime(Preflight preflight, HostSetup host)
    {
        // CLI shim 注册（ADR simple-shell-single-global-dsh）：dsh 已全局在 PATH，仅注册 pnpm shim。
        // best-effort——注册内部吞预期异常（见 CliShimRegistrar），此处再兜底意外异常。
        preflight.Bootstrap.RegisterCliShim();

        DshWebUrl? webUrl = preflight.Bootstrap.IsNeeded
            ? null
            : DshWebUrl.FromNullable(host.Host.StartAsync(timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult());
        if (!preflight.Bootstrap.IsNeeded)
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

        return new RuntimeSetup(webUrl);
    }

    private int RunAppLoop(Preflight preflight, AppSetup app, SupervisorSetup supervisor)
    {
        HostLog.Write("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            app.App.Run();
        }
        catch (Exception ex)
        {
            HostLog.Write($"[host] Ryn Run 异常：{ex}");
        }

        HostLog.Write("[host] Ryn Run 结束");
        supervisor.Cts.Cancel();
        preflight.Bootstrap.Cancel();
        try
        {
            supervisor.Task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 监督任务随宿主回收而结束；无需上报
        }

        // 非 orderly 退出路径（用户直接关窗使 Run 返回）与托盘有序退出共用同一单实例管道：
        // once-guard 使已回收路径的重复调用为 no-op
        _exit.ReapRuntime();
        // 非 orderly 退出路径也要释放单实例锁地址：orderly 路径已 Dispose 过，幂等守卫保证此处安全
        _instanceListener?.Dispose();
        return 0;
    }
}
