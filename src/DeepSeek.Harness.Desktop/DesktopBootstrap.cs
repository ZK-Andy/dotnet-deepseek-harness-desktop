using DeepSeek.Harness.Desktop.Services;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 桌面壳组合根（ADR split-program-main-god-function）：承载原 <c>Program.Main</c> 的全部编排。
/// 纯抽取、零行为变更——语句顺序/分支/异常边界与原 Main 逐一对应；共享状态为字段（_camelCase）、
/// 编排方法按关注面拆为 partial（App/Lifecycle），本文件承载核心入口与装配前奏。
/// </summary>
public sealed partial class DesktopBootstrap
{
    // —— 共享状态（原 Main 局部变量 → 字段；赋值时点与原 Main 语句位置一致，语义等价）——
    private bool _bootstrapNeeded;
    private Services.RuntimeBootstrapOptions? _bootstrapOptions;
    private Services.RuntimeBootstrapGate _bootstrapGate = null!;
    private Services.PreinstallChoiceGate _preinstallGate = null!;
    private TaskCompletionSource? _bootstrapSettled;
    private CancellationTokenSource? _bootstrapCts;
    private bool _isDev;
    private bool _devAutoIsolated;
    private int _maximizedAtHide = -1;
    private Action? _updateExitReaper;
    // _supervisorCtsRef = _supervisorCts 的第二个引用（非恒等别名，勿删）：命令路由在 BuildApp 的
    // RegisterServices 注册时点早于 SetupSupervisor 给 _supervisorCts 赋值，故先持一个可空引用、
    // 由 SetupSupervisor 接线（Lifecycle 行 198），路由经 backgroundToken 闭包惰性读它——
    // 服务注册期拿不到 supervisorCts 的延迟捕获模式。
    private CancellationTokenSource? _supervisorCtsRef;
    private Services.UiLocale _uiLocale = null!;
    private PrimaryListener? _instanceListener;
    private Services.Update.UpdateStateMachine? _updateMachine;
    private bool _readyNotified;
    private string _iconPath = null!;
    private bool _trayAvailable;
    private bool _trayReady;
    private Action? _orderlyQuit;
    private Services.PageHealthMonitor? _healthMonitor;
    private RynApplication _app = null!;
    private CurrentWindowAccessor _windowAccessor = null!;
    private Uri? _webUrl;
    private HarnessRuntimeHost _host = null!;
    private RunMarkerResult _marker = null!;
    private bool _previousRunUnclean;
    private CancellationTokenSource _supervisorCts = null!;
    private RuntimeSupervisor _supervisor = null!;
    private Task _supervisorTask = null!;
    private Services.Tray.CloseGate _closeGate = null!;
    private Services.Tray.CloseBehaviorPreference _closeBehavior = null!;
    private bool _updateEnabled;

    /// <summary>组合根入口：按原 <c>Program.Main</c> 语句序执行全部编排并返回进程退出码。</summary>
    public int Run()
    {
        ResolveRuntimeAndDev();
        if (!AcquireSingleInstance())
        {
            return 0;
        }

        EnsureDesktopProfile();
        try
        {
            SetupHostAndMarker();
            InstallCompanionBeforeSpawn();
            StartRuntime();
            InitCloseGateAndUpdateStack();
            BuildApp();
            RunBootstrapIfNeeded();
            ShowTray();
            SetupSupervisor();
            SetupHealthMonitor();
            StartUpdateCheck();
            SharedHomeBannerTask();
            return RunAppLoop();
        }
        finally
        {
            // 原 `using var host` / `using var supervisorCts` 作用域到 Main 末尾；这里在 Run 末尾等价释放。
            _host?.Dispose();
            _supervisorCts?.Dispose();
        }
    }

    private void ResolveRuntimeAndDev()
    {
        // 运行时来源（ADR simple-shell-single-global-dsh）：桌面是简单壳，依赖全机唯一的系统全局 node +
        // 全局 dsh（都在 PATH 上），桌面与终端共用同一套；没有系统 node 时由桌面装 node 到系统全局前缀
        //（需 sudo 则提示手动命令），而非桌面包私有运行时/私有 PATH。
        // 引导参数解析一次，供本方法（EnsureRuntimeNodeOnPath）与 RegisterCliShim/RunBootstrapWithRetry 复用。
        _bootstrapOptions = Services.RuntimeBootstrapOptions.Load(AppContext.BaseDirectory);
        // 若系统全局 node 已由桌面装好（此前安装/用户手动），把它暴露到进程 PATH，让宿主 spawn 与探测能解析。
        EnsureRuntimeNodeOnPath();

        // 启动只读探测 PATH 上全局 dsh——没有 → 走首启引导（npm install -g 装/更新到 @alpha）。
        // dev 判定只认显式环境标记（DSH_DESKTOP_RUNTIME_DIR / DSH_DESKTOP_DEV=1）——绝不以
        // 捆绑闭包存在性探测（打包新装同样没有闭包，探测会误判全部新装用户）。
        _bootstrapNeeded = false;
        {
            string? pathVersion = Services.RuntimeVersionGate.ProbeAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            // 没有 → 装；落后 alpha 兼容底线（低于底部）→ 经引导更新到 @alpha；否则直接用。
            // 引导内 npm install -g @alpha 幂等（安装或更新）；检测"落后"以兼容底线（MinimumVersion）为
            // 廉价代理——精确对齐 @alpha 需启动时查询 npm dist-tag（见实现受阻点/决策点）。
            _bootstrapNeeded = pathVersion is null || Services.RuntimeVersionGate.IsBelowFloor(pathVersion);
            Services.HostLog.Write(_bootstrapNeeded
                ? $"[bootstrap] 全局 dsh 未检出或落后（{pathVersion ?? "(无)"}），进入首启引导"
                : $"[bootstrap] 全局 dsh 可用（{pathVersion}），跳过首启引导");
        }

        // 引导共享状态（声明提前：命令路由注册、监督器门控、插件安装门控、后台引导任务共用）。
        // gate 供引导页 desktop.bootstrap.retry 命令放行重试循环；settled 在引导终态（成功/失败放弃/
        // 取消）置位——监督器与插件安装都等它，防引导期误拉 dsh 或误装插件。
        _bootstrapGate = new Services.RuntimeBootstrapGate();
        // 插件引导决策闸门（ADR reference-alignment 批次二）：引导页 desktop.preinstall.choose
        // 命令置位「确认装/跳过」，引导任务 await Choice 消费。与 bootstrapGate 同款声明提前——
        // 命令路由注册、引导任务共用同一实例。
        _preinstallGate = new Services.PreinstallChoiceGate();
        string? devRuntimeDir = Environment.GetEnvironmentVariable(DevEnvironment.RuntimeDirEnv);
        string? devFlag = Environment.GetEnvironmentVariable(DevEnvironment.DevFlagEnv);
        _isDev = DevEnvironment.IsDevRuntime(devRuntimeDir, devFlag);
        _devAutoIsolated = false;
        if (_isDev && Environment.GetEnvironmentVariable(DevEnvironment.HomeOverrideEnv) is null)
        {
            string? devHome = DevEnvironment.DeriveDefaultDevHome(devRuntimeDir, AppContext.BaseDirectory);
            if (devHome is not null)
            {
                Environment.SetEnvironmentVariable(DevEnvironment.HomeOverrideEnv, devHome);
                _devAutoIsolated = true;
                Services.HostLog.Write($"[host] 开发运行时：DSH_HOME 隔离到 {devHome}；ApplicationId 带 .dev 后缀，可与正式版并存");
            }
        }
        else if (!_isDev &&
                 DevEnvironment.DeriveDefaultDevHome(null, AppContext.BaseDirectory) is not null)
        {
            // dev 判定改显式标记后的唯一残留风险（R2 评审）：贡献者在仓库内跑却忘带
            // DSH_DESKTOP_DEV=1 —— 判定按设计走打包产品语义，但值得一条 host.log 诊断指路
            Services.HostLog.Write("[host] 疑似仓库内开发运行但未设 DSH_DESKTOP_DEV=1：按打包产品处理（共享真实 home，无 dev 隔离）");
        }
    }

    /// <summary>把"系统全局 node 的 global bin"暴露到进程 PATH（ADR simple-shell-single-global-dsh：无系统 node
    /// 时由桌面把 node 装到系统全局前缀，桌面与终端共用同一份）。node 已装到全局前缀时生效，否则 no-op。</summary>
    private void EnsureRuntimeNodeOnPath()
    {
        if (Services.RuntimeBootstrap.TryResolveActiveNodeBinDir(_bootstrapOptions!) is { } nodeBin)
        {
            Services.RuntimeBootstrap.PrependPathToProcessEnv(nodeBin);
            Services.HostLog.Write($"[host] 系统全局 node 已暴露到 PATH：{nodeBin}");
        }
    }

    /// <summary>单实例仲裁（ADR single-instance-launcher-activation）：false = 已有主实例，调用方直接返回 0。</summary>
    private bool AcquireSingleInstance()
    {
        // hide-to-tray 唤回的最大化保持样本（ADR tray-recall-maximize-and-check-feedback）：
        // 隐藏前采样（1=最大化 / 0=非 / -1=未知），唤回路径按判据消费后在 finally 无条件清零。
        // 声明置于最前：launcher 激活回调与托盘召回共用同一份样本语义，激活唤起同样消费，
        // 防残留样本让下一次托盘点击把用户手动还原的窗口误最大化。
        _maximizedAtHide = -1;
        // 宿主 UI 语言单点（ADR host-ui-locale）：companion 上报 dsh locale，托盘/横幅据此出双语
        _uiLocale = new Services.UiLocale();
        string? xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? instanceSocketPath = OperatingSystem.IsWindows()
            ? null // Windows 无验证环境不启用互斥，行为维持现状（ADR 平台边界）
            : LauncherActivation.SocketPath(
                xdgRuntimeDir is { Length: > 0 } ? xdgRuntimeDir : Path.GetTempPath(),
                "deepseek-harness-desktop" +
                (xdgRuntimeDir is { Length: > 0 } ? string.Empty : LauncherActivation.FallbackUidSuffix()),
                _isDev);
        if (instanceSocketPath is not null)
        {
            if (!LauncherActivation.TryBindPrimary(
                    instanceSocketPath,
                    onShowRequested: async () =>
                    {
                        // BuildApp 前 _windowAccessor 尚未赋值（null! 占位）：启动早期到达的
                        // 激活请求静默忽略（保留原可空窗口字段语义）。
                        CurrentWindowAccessor? accessor = _windowAccessor;
                        if (accessor is null)
                        {
                            return;
                        }

                        try
                        {
                            await accessor.Current.ShowAsync();
                            Services.HostLog.Write("[host] launcher 激活：显示主窗完成");
                        }
                        catch (Exception ex)
                        {
                            Services.HostLog.Write($"[host] launcher 激活显示主窗失败：{ex.Message}");
                        }
                        finally
                        {
                            // 与托盘唤回同一消费契约：无论显示成败都清样本——残留会让下一次
                            // 托盘点击把用户手动还原的窗口误最大化
                            Volatile.Write(ref _maximizedAtHide, -1);
                        }
                    },
                    Services.HostLog.Write,
                    out _instanceListener))
            {
                bool notified = LauncherActivation.NotifyPrimary(instanceSocketPath, TimeSpan.FromSeconds(2));
                Services.HostLog.Write(
                    $"[host] 已有主实例在运行（launcher 二次启动）：通知显示主窗{(notified ? "成功" : "未达（主实例可能正忙）")}，本次启动退出");
                return false;
            }
        }

        return true;
    }

    private void EnsureDesktopProfile()
    {
        // 桌面专属 profile 自举（ADR shared-home-desktop-profile）：上游对自定义 profile 名不自动初始化，
        // 缺清单直接拒启；必须在 spawn 前确保 desktop profile 就绪（幂等，已存在则零写入）。
        try
        {
            if (DesktopProfileBootstrap.EnsureProfile(HarnessRuntimeHost.ResolveDshHome()))
            {
                Services.HostLog.Write("[host] 已初始化 profiles/desktop（bundles 对齐 web 模板）");
            }

            // 启动前 reconcile 不可解析的 bundle 引用（ADR online-first-unbundled-runtime 批次三，
            // 对齐 dsh-tauri-desk #177：退役随包种子后，存量 profile 可能残留指向已消失 tgz 的
            // file:/link: 引用，dsh 启动时视作不可解析 → 卡死循环）。必须在 spawn 前清理。
            int reconciled = DesktopProfileBootstrap.ReconcileProfile(HarnessRuntimeHost.ResolveDshHome(), Services.HostLog.Write);
            if (reconciled > 0)
            {
                Services.HostLog.Write($"[host] 桌面 profile reconcile：移除 {reconciled} 个不可解析插件引用");
            }
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[host] profiles/desktop 初始化失败（dsh 可能拒启，详见后续降级链路）：{ex.Message}");
        }
    }

    private void SetupHostAndMarker()
    {
        // 原 `using var host`：生命周期由 Run 的 finally 释放（本方法赋值）。
        // 全局 dsh 模型：宿主恒以 PATH dsh（bundled=null）形态运行（ADR simple-shell-single-global-dsh）。
        _host = new HarnessRuntimeHost(Services.HostLog.Write);

        // 崩溃取证 marker（ADR shell-observability-diagnostics）：遗留即判定上轮非受控退出；
        // 正常退出路径在 Run 尾部按 token 清除
        _marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());
        _previousRunUnclean = _marker.PreviousRunUnclean;
        if (_previousRunUnclean)
        {
            Services.HostLog.Write("[host] 检测到上轮未正常退出的标记；如频繁出现请在设置页导出诊断信息");
        }
    }

    private void InstallCompanionBeforeSpawn()
    {
        // 对齐参照（dsh-tauri-desk launch.rs）：随包插件（companion）在 spawn dsh 前安装，绝不
        // 「启动后 3s 装 → 重启」。全局 dsh 模型（ADR simple-shell-single-global-dsh）：dsh 在 PATH 上，
        // nodeExe/dshEntry 传 null，EnsureBundledPluginsBeforeSpawnAsync 内回退到 PATH 上的 dsh 命令。
        // dev 显式覆盖共享 home 时跳过（防串扰）。
        if (!_bootstrapNeeded && !(_isDev && !_devAutoIsolated))
        {
            try
            {
                Services.MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                    nodeExe: null,
                    dshEntry: null,
                    HarnessRuntimeHost.ResolveDshHome(),
                    Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                    Services.HostLog.Write,
                    Services.PluginProcessRunner.RunAsync,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Services.HostLog.Write($"[host] 随包插件 spawn 前安装失败（跳过，不阻断启动）：{ex.Message}");
            }
        }
    }

    private void StartRuntime()
    {
        // CLI shim 注册（ADR simple-shell-single-global-dsh）：dsh 已全局在 PATH，仅注册 pnpm shim。
        // best-effort——注册内部吞预期异常（见 CliShimRegistrar），此处再兜底意外异常。
        RegisterCliShim();

        _webUrl = _bootstrapNeeded
            ? null
            : _host.StartAsync(timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (!_bootstrapNeeded)
        {
            Services.HostLog.Write($"[host] runtime = {_host.RuntimeDescription}");
            if (_webUrl is not null)
            {
                Services.HostLog.Write($"[host] dsh web = {_webUrl}");
            }
            else
            {
                Services.HostLog.Write($"[host] dsh 未在时限内给出 URL；降级加载 wwwroot。stderr 尾巴：\n{string.Join('\n', _host.StderrTail.TakeLast(8))}");
            }
        }
    }

    private int RunAppLoop()
    {
        Services.HostLog.Write("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            _app.Run();
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[host] Ryn Run 异常：{ex}");
        }

        Services.HostLog.Write("[host] Ryn Run 结束");
        _supervisorCts.Cancel();
        _bootstrapCts?.Cancel();
        try
        {
            _supervisorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 监督任务随宿主回收而结束；无需上报
        }

        _host.Stop();
        RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), _marker.Token);
        // 非 orderly 退出路径（用户直接关窗使 Run 返回）也要释放单实例锁地址：
        // orderly 路径已 Dispose 过，幂等守卫保证此处安全
        _instanceListener?.Dispose();
        return 0;
    }
}
