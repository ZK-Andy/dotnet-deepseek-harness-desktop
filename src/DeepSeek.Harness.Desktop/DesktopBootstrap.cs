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
    // —— 共享状态（组合根只保留顺序资源与服务引用；域状态已下沉真类型服务）——
    private IFirstBootBootstrap _bootstrap = null!;
    private bool _isDev;
    private bool _devAutoIsolated;
    private Core.ExitPipeline _exit = null!;
    // _supervisorCtsRef = _supervisorCts 的第二个引用（非恒等别名，勿删）：命令路由在 BuildApp 的
    // RegisterServices 注册时点早于 SetupSupervisor 给 _supervisorCts 赋值，故先持一个可空引用、
    // 由 SetupSupervisor 接线（Lifecycle 行 198），路由经 backgroundToken 闭包惰性读它——
    // 服务注册期拿不到 supervisorCts 的延迟捕获模式。
    private CancellationTokenSource? _supervisorCtsRef;
    private UiLocale _uiLocale = null!;
    private PrimaryListener? _instanceListener;
    private Services.Tray.TrayController _tray = null!;
    private Services.PageHealthMonitor? _healthMonitor;
    private RynApplication _app = null!;
    private CurrentWindowAccessor _windowAccessor = null!;
    private Uri? _webUrl;
    private HarnessRuntimeHost _host = null!;
    private RunMarkerResult _marker = null!;
    private CancellationTokenSource _supervisorCts = null!;
    private RuntimeSupervisor _supervisor = null!;
    private Task _supervisorTask = null!;
    private Services.Update.UpdateCoordinator _updates = null!;

    // —— 启动编排阶段 token（ADR composition-root-stage-typing）——
    // 组合根的启动阶段存在严格依赖序，旧形态靠"注释 + 字段赋值时点"维持、编译器不检查。
    // 阶段方法签名收上一阶段 token、返回本阶段 token，把**产生者的执行序**钉进类型——
    // 想跨过产生者直接用其产出（如不经 StartRuntime 拿 webUrl）在编译期即缺值不可用。
    // 保护是**偏序非全序**：token 锁产生者必须先跑，但不锁消费段——整段漏调消费方法
    //（如删 ShowTray）、或两个同 token 消费段乱序，编译仍通过（与重构前同风险，非本
    // 机制承诺面）。唯一例外：`UpdateToken` 被 BuildApp 消费，省略 InitCloseGateAndUpdateStack
    // 会让 BuildApp 缺参编译失败——这是 6 个 token 中唯一"缺失即编译失败"的硬约束，勿简化掉。
    // token 为空载荷 marker（空 record struct）：只承载"本阶段已执行"的类型承诺，实际状态
    // 仍留在字段（后台 Task 闭包/DI 回调/事件委托大量跨阶段捕获字段——下沉为参数即字段归属
    // 迁移，违反 ADR "不迁移字段归属"纪律）。私有嵌套：只作组合根签名间传递，不构成根命名
    // 空间独立类型（A5 门禁放行依据）。
    private readonly record struct PreflightToken;      // ResolveRuntimeAndDev 完成：运行时/env/单实例前提已解析
    private readonly record struct HostToken;           // SetupHostAndMarker 完成：host/marker 已建
    private readonly record struct RuntimeToken;        // StartRuntime 完成：dsh web 已起（webUrl 落字段）
    private readonly record struct UpdateToken;         // InitCloseGateAndUpdateStack 完成：关窗闸门/自更新栈已建
    private readonly record struct AppToken;            // BuildApp 完成：Ryn 应用与 windowAccessor 就绪
    private readonly record struct SupervisorToken;     // SetupSupervisor 完成：监督器/退出编排已接线

    /// <summary>组合根入口：按原 <c>Program.Main</c> 语句序执行全部编排并返回进程退出码。</summary>
    public int Run()
    {
        PreflightToken preflight = ResolveRuntimeAndDev();
        if (!AcquireSingleInstance(preflight))
        {
            return 0;
        }

        EnsureDesktopProfile();
        try
        {
            HostToken host = SetupHostAndMarker(preflight);
            InstallCompanionBeforeSpawn(host);
            RuntimeToken runtime = StartRuntime(host);
            UpdateToken update = InitCloseGateAndUpdateStack(runtime);
            AppToken app = BuildApp(runtime, update);
            RunBootstrapIfNeeded(app);
            ShowTray(app);
            SupervisorToken supervisor = SetupSupervisor(app, host);
            SetupHealthMonitor(supervisor);
            StartUpdateCheck(supervisor);
            SharedHomeBannerTask(supervisor);
            return RunAppLoop(supervisor, host);
        }
        finally
        {
            // 原 `using var host` / `using var supervisorCts` 作用域到 Main 末尾；这里在 Run 末尾等价释放。
            _host?.Dispose();
            _supervisorCts?.Dispose();
        }
    }

    private PreflightToken ResolveRuntimeAndDev()
    {
        // 首启引导服务（R3 端口实现，ADR composition-root-value-flow-pipeline 批次 1）：全局 node/dsh
        // 引导、插件装配、CLI shim、宿主启动从组合根下沉；页面反馈经 FirstBootUi 注入，宿主惰性提供。
        _bootstrap = new FirstBootBootstrapService(
            () => _host,
            new Services.FirstBootUi(() => _windowAccessor),
            HostLog.Write);
        _bootstrap.Resolve();

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
                HostLog.Write($"[host] 开发运行时：DSH_HOME 隔离到 {devHome}；ApplicationId 带 .dev 后缀，可与正式版并存");
            }
        }
        else if (!_isDev &&
                 DevEnvironment.DeriveDefaultDevHome(null, AppContext.BaseDirectory) is not null)
        {
            // dev 判定改显式标记后的唯一残留风险（R2 评审）：贡献者在仓库内跑却忘带
            // DSH_DESKTOP_DEV=1 —— 判定按设计走打包产品语义，但值得一条 host.log 诊断指路
            HostLog.Write("[host] 疑似仓库内开发运行但未设 DSH_DESKTOP_DEV=1：按打包产品处理（共享真实 home，无 dev 隔离）");
        }

        // token：ResolveRuntimeAndDev 完成（配置已落字段），供后续阶段按类型承诺串联。
        return default;
    }

    /// <summary>单实例仲裁（ADR single-instance-launcher-activation）：false = 已有主实例，调用方直接返回 0。
    /// 依赖 <paramref name="preflight"/>（ResolveRuntimeAndDev 产出的 _isDev 已解析）。</summary>
    private bool AcquireSingleInstance(PreflightToken preflight)
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
                _isDev);
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

    private HostToken SetupHostAndMarker(PreflightToken preflight)
    {
        // 原 `using var host`：生命周期由 Run 的 finally 释放（本方法赋值）。
        // 全局 dsh 模型：宿主恒以 PATH dsh（bundled=null）形态运行（ADR simple-shell-single-global-dsh）。
        _host = new HarnessRuntimeHost(HostLog.Write);

        // 崩溃取证 marker（ADR shell-observability-diagnostics）：遗留即判定上轮非受控退出；
        // 正常退出路径在 Run 尾部按 token 清除
        _marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());
        if (_marker.PreviousRunUnclean)
        {
            HostLog.Write("[host] 检测到上轮未正常退出的标记；如频繁出现请在设置页导出诊断信息");
        }

        // token：SetupHostAndMarker 完成（host/marker 已落字段），供后续阶段按类型承诺串联。
        return default;
    }

    private void InstallCompanionBeforeSpawn(HostToken host)
    {
        // 对齐参照（dsh-tauri-desk launch.rs）：随包插件（companion）在 spawn dsh 前安装，绝不
        // 「启动后 3s 装 → 重启」。全局 dsh 模型（ADR simple-shell-single-global-dsh）：dsh 在 PATH 上，
        // nodeExe/dshEntry 传 null，EnsureBundledPluginsBeforeSpawnAsync 内回退到 PATH 上的 dsh 命令。
        // dev 显式覆盖共享 home 时跳过（防串扰）。
        if (!_bootstrap.IsNeeded && !(_isDev && !_devAutoIsolated))
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

    private RuntimeToken StartRuntime(HostToken host)
    {
        // CLI shim 注册（ADR simple-shell-single-global-dsh）：dsh 已全局在 PATH，仅注册 pnpm shim。
        // best-effort——注册内部吞预期异常（见 CliShimRegistrar），此处再兜底意外异常。
        _bootstrap.RegisterCliShim();

        _webUrl = _bootstrap.IsNeeded
            ? null
            : _host.StartAsync(timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (!_bootstrap.IsNeeded)
        {
            HostLog.Write($"[host] runtime = {_host.RuntimeDescription}");
            if (_webUrl is not null)
            {
                HostLog.Write($"[host] dsh web = {_webUrl}");
            }
            else
            {
                HostLog.Write($"[host] dsh 未在时限内给出 URL；降级加载 wwwroot。stderr 尾巴：\n{string.Join('\n', _host.StderrTail.TakeLast(8))}");
            }
        }

        // token：StartRuntime 完成（webUrl 已落字段），供后续阶段按类型承诺串联。
        return default;
    }

    private int RunAppLoop(SupervisorToken supervisor, HostToken host)
    {
        HostLog.Write("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            _app.Run();
        }
        catch (Exception ex)
        {
            HostLog.Write($"[host] Ryn Run 异常：{ex}");
        }

        HostLog.Write("[host] Ryn Run 结束");
        _supervisorCts.Cancel();
        _bootstrap.Cancel();
        try
        {
            _supervisorTask.Wait(TimeSpan.FromSeconds(2));
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
