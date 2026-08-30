using System.Runtime.InteropServices;
using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Callbacks;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 桌面壳组合根（ADR split-program-main-god-function）：承载原 <c>Program.Main</c> 的全部编排。
/// 纯抽取、零行为变更——语句顺序/分支/异常边界与原 Main 逐一对应；共享状态为字段（_camelCase）、
/// 局部函数为私有方法。辅助方法见 <c>DesktopBootstrap.Startup.cs</c>（partial）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    // —— 共享状态（原 Main 局部变量 → 字段；赋值时点与原 Main 语句位置一致，语义等价）——
    private (string NodeExe, string DshEntry)? _bundledClosure;
    private bool _bootstrapNeeded;
    private Services.RuntimeBootstrapGate _bootstrapGate = null!;
    private Services.PreinstallChoiceGate _preinstallGate = null!;
    private TaskCompletionSource? _bootstrapSettled;
    private CancellationTokenSource? _bootstrapCts;
    private bool _isDev;
    private bool _devAutoIsolated;
    private int _maximizedAtHide = -1;
    // TODO(window-accessor-alias): _updateWindow 是 _windowAccessor 的恒等别名（BuildApp 末赋值一次，
    // 永不分化）。可折叠到 _windowAccessor（保留 167 处的空检注释）；现按「赋值时点与原 Main 一致」
    // 不变量如实保留，属拆分后应收口的残渣。
    private CurrentWindowAccessor? _updateWindow;
    private Action? _updateExitReaper;
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
    // TODO(navigation-settled-unconsumed): _startupNavigationSettled 只 TrySetResult、无任何消费者
    // （横幅走 ShowBannerWhenReady 的重试环、恢复走 showRecovery 直注入）。wire-or-cut——要么删字段+接线
    // （零行为变更），要么真接回横幅门控；现状「写信号不读」误导读者。
    private TaskCompletionSource _startupNavigationSettled = null!;
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
        // 开发运行时完全隔离（ADR dev-runtime-isolation）：ApplicationId 加 .dev 后缀避开
        // GTK 同 id 单实例互斥（与已装正式版可同时开窗）；DSH_HOME 未显式覆盖时自动指向
        // 仓库 .cache/dev-home，杜绝与正式版共享 profile 的串扰。
        // dev 判定只认显式环境标记（DSH_DESKTOP_RUNTIME_DIR / DSH_DESKTOP_DEV=1）——绝不以
        // 捆绑闭包存在性探测（online-first 后打包新装同样没有闭包，探测会误判全部新装用户，
        // ADR online-first-unbundled-runtime 批次二收口 shared-home 挂账）。
        string updateRuntimeDir = RuntimeLocator.ResolveRuntimeDirectory();
        _bundledClosure = RuntimeLocator.TryLocateBundled(updateRuntimeDir);

        // 首启引导判定（ADR online-first-unbundled-runtime）：捆绑运行时缺失且 PATH dsh 不可用
        // 时进入引导（下载钉版 Node + registry 安装 dsh）。检测是毫秒级只读探测，同步执行。
        _bootstrapNeeded = false;
        if (_bundledClosure is null)
        {
            string? pathVersion = Services.RuntimeVersionGate.ProbeAsync(null, CancellationToken.None)
                .GetAwaiter().GetResult();
            _bootstrapNeeded = pathVersion is null;
            Services.HostLog.Write(_bootstrapNeeded
                ? "[bootstrap] 捆绑运行时与 PATH dsh 均未检出，进入首启引导"
                : $"[bootstrap] PATH dsh 可用（{pathVersion}），跳过首启引导");
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
        else if (!_isDev && _bundledClosure is null &&
                 DevEnvironment.DeriveDefaultDevHome(null, AppContext.BaseDirectory) is not null)
        {
            // dev 判定改显式标记后的唯一残留风险（R2 评审）：贡献者在仓库内跑却忘带
            // DSH_DESKTOP_DEV=1 —— 判定按设计走打包产品语义，但值得一条 host.log 诊断指路
            Services.HostLog.Write("[host] 疑似仓库内开发运行但未设 DSH_DESKTOP_DEV=1：按打包产品处理（共享真实 home，无 dev 隔离）");
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
                        CurrentWindowAccessor? accessor = _updateWindow;
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
        _host = new HarnessRuntimeHost(_bundledClosure, Services.HostLog.Write);

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
        // 「启动后 3s 装 → 重启」。覆盖两种运行时：捆绑闭包（node <dshEntry>）与 PATH-dsh（dsh 命令，
        // bundledClosure 为 null 时 nodeExe/dshEntry 传 null，EnsureBundledPluginsBeforeSpawnAsync 内
        // 回退到 PATH 上的 dsh）。v0.4.0 实机回归：旧门控要求 bundledClosure is not null，漏了「有 PATH
        // dsh、无捆绑闭包」——online-first 升级存量用户（v0.3.x companion 是 file: resources/runtime 引用、
        // 已被 reconcile 移除）时 companion 永不重新安装。dev 显式覆盖共享 home 时跳过（防串扰）。
        if (!_bootstrapNeeded && !(_isDev && !_devAutoIsolated))
        {
            try
            {
                Services.MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                    _bundledClosure?.NodeExe,
                    _bundledClosure?.DshEntry,
                    HarnessRuntimeHost.ResolveDshHome(),
                    Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                    Services.HostLog.Write,
                    RunDshPluginAddAsync,
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
        // CLI shim 注册（ADR reference-alignment 批次四）：运行时就位后把 dsh/pnpm 注册进用户
        // PATH，让终端可直用。best-effort——注册内部吞预期异常（见 CliShimRegistrar），此处再兜底
        // 意外异常；dev 隔离时跳过 dsh shim（防把开发环境烘焙进共享 shim）。
        RegisterCliShim(_isDev);

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

    private void InitCloseGateAndUpdateStack()
    {
        // hide-to-tray 关窗闸门（ADR shell-tray-hide-to-tray）：托盘「退出」与自更新安装路径
        // 先批准再 Close。用户普通关窗是否转隐藏由 closeBehavior 偏好裁决（默认 true 保持
        // 历史行为）；托盘未就绪时拦截不生效（关窗直退）。
        _closeGate = new Services.Tray.CloseGate();
        _closeBehavior = new Services.Tray.CloseBehaviorPreference(
            Path.Combine(HarnessRuntimeHost.ResolveDshHome(), Services.Tray.CloseBehaviorPreference.FileName));

        // 自更新栈（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）：
        // 状态机纯逻辑可单测，检查/下载/安装全部委托注入；状态经 CustomEvent 推给插件 UI。
        // dev 运行时不装载（除非 DSH_DESKTOP_UPDATE_FORCE=1 显式开启验证）：dev 构建版本同 csproj，
        // 一旦比对出新 release，点击会把官方包装进系统后按 Environment.ProcessPath 拉起**旧 dev 二进制**，
        // 版本不变、ready 记录不清，形成循环（审核加固，见 ADR self-update-review-hardening）。
        _updateEnabled = Services.Update.UpdateOptions.IsEnabledFor(
            _isDev,
            Environment.GetEnvironmentVariable(Services.Update.UpdateOptions.ForceDevEnv));
        _updateMachine = null;
        _readyNotified = false;
        if (_updateEnabled)
        {
            var updateOptions = Services.Update.UpdateOptions.Load(AppContext.BaseDirectory);
            var updateHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            string updatesDir = Path.Combine(HarnessRuntimeHost.ResolveDshHome(), updateOptions.UpdatesDirName);
            string? updatePkgKind = Services.Update.UpdatePlatform.DetectCurrentPackageKind();
            _updateMachine = new Services.Update.UpdateStateMachine(
                currentVersion: Services.Update.AppVersion.Current(),
                check: ct => new Services.Update.ReleaseMetaClient(updateHttp, updateOptions, Services.HostLog.Write).FetchLatestAsync(UpdateRid(), updatePkgKind, ct),
                download: (meta, ct) => new Services.Update.InstallerDownloader(updateHttp, Services.HostLog.Write).DownloadAsync(
                    meta, updatesDir, TimeSpan.FromMinutes(updateOptions.DownloadTimeoutMinutes), ct),
                install: async (assetPath, version, ct) =>
                {
                    // 安装时点自 release SHA256SUMS 复取期望哈希（HTTPS 直达仓库，用户空间改写不了）：
                    // root 侧装前复验对照它——落盘的任何哈希都可被同权限改写，唯有 release 侧值是锚点。
                    // 离线时此处抛出拒装（状态机回 ready），不无复核安装。
                    string expectedSha = await new Services.Update.InstallerDownloader(updateHttp, Services.HostLog.Write)
                        .FetchSha256Async(updateOptions.Repository, version, Path.GetFileName(assetPath), ct);
                    // 授权通过（LaunchAsync 观察窗口内未取消）后：主动关闭窗口让进程退出，
                    // 安装脚本的等待环随即放行 rpm/dpkg 并拉起新版。缺这步脚本会死等本进程。
                    await Services.Update.UpdateInstaller.LaunchAsync(assetPath, updatesDir, expectedSha, ct, log: Services.HostLog.Write);
                    Services.HostLog.Write("[update] 授权通过，关闭应用以继续安装…");
                    // 安装路径与托盘退出共用闸门：先批准，Close 才不会被 hide-to-tray 拦截转成隐藏
                    _closeGate.ApproveExit();
                    try
                    {
                        _updateWindow?.Current?.Close();
                    }
                    catch (Exception ex)
                    {
                        Services.HostLog.Write($"[update] 窗口关闭失败：{ex.Message}");
                    }

                    // 兜底：8 秒内仍未退出（Close 事件丢失等）则强制退出，保证安装流程放行
                    StartExitFallback(ct);
                },
                persistence: new Services.Update.FileReadyPersistence(updatesDir),
                onTransition: state =>
                {
                    // 自更新链路留痕：每次状态变化进 host.log（stdout 不可见教训的统一收口）
                    Services.HostLog.Write(
                        "[update] " + state.Status
                        + (state.Version is null ? "" : $" {state.Version}")
                        + (state.Message is null ? "" : $"：{state.Message}"));
                    PushUpdateState(_updateWindow, state);
                });
            Services.HostLog.Write($"[host] 自更新：当前版本 {Services.Update.AppVersion.Current()}，RID {UpdateRid()}，包类型 {updatePkgKind ?? "(n/a)"}，目录 {updatesDir}，feed 超时 {updateOptions.FeedTimeoutSeconds}s 下载超时 {updateOptions.DownloadTimeoutMinutes}m");
        }
        else
        {
            Services.HostLog.Write("[host] 自更新：dev 运行时不装载（DSH_DESKTOP_UPDATE_FORCE=1 可显式开启）");
        }
    }

    private void BuildApp()
    {
        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 trayReady）
        _iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        _trayAvailable = File.Exists(_iconPath);

        _app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                if (_webUrl is not null)
                {
                    // dsh web UI（loopback；完整运行时随应用内置后仍是此路径）
                    opts.Url = _webUrl;
                }
                else
                {
                    // 降级：dsh 未起时展示本地占位页，保证壳仍可开
                    opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                }

                opts.Title = "DeepSeek Harness Desktop";
                opts.Width = 1200;
                opts.Height = 800;
                opts.ApplicationId = DevEnvironment.ApplicationIdFor(
                    "io.github.ZK-Andy.dotnet-deepseek-harness-desktop", _isDev);
                if (File.Exists(_iconPath))
                {
                    opts.IconPath = _iconPath;
                }
                else
                {
                    Services.HostLog.Write($"[host] icon 缺失：{_iconPath}");
                }

                Services.HostLog.Write($"[host] Ryn opts: Url={(_webUrl is not null ? _webUrl.ToString() : "null")} ApplicationId={opts.ApplicationId} Icon={(File.Exists(_iconPath) ? _iconPath : "missing")}");
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(RegisterServices)
            .Build();

        _windowAccessor = _app.Services.GetRequiredService<CurrentWindowAccessor>();
        _updateWindow = _windowAccessor;
    }

    private void RegisterServices(IServiceCollection services)
    {
        services.AddRynCommands();
        // 宿主导航回调（Ryn 0.32.0 Ryn.Callbacks）：在导航边界统一拦截外部链接，
        // 并给崩溃恢复/横幅门控提供「页面已到达」信号（ADR ryn-navigation-callbacks）。
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        // 覆盖源生成的 handler 无参注册：导航回调依赖（openExternal 打开器 / 日志 /
        // 当前页面 origin）在 ConfigureServices 时已知，经工厂注入；onNavigated 之后经
        // SetOnNavigated 绑定（startupNavigationSettled 在其后声明）。
        services.AddSingleton(sp => new Services.RynNavigationCallbacks(
            opener: null,
            log: Services.HostLog.Write,
            currentOrigin: _webUrl?.GetLeftPart(UriPartial.Authority),
            // 外部链接打开失败 → 推事件给页面，companion 渲染 toast（R2 N2）。EmitEvent 走
            // deferred IRynWebView（窗口就绪后转发），在导航回调触发时页面必然已加载。
            notifyLinkFail: url => sp.GetRequiredService<IRynWebView>().EmitEvent(
                "desktop.externalLinkOpenerFailed",
                new Services.ExternalLinkOpenerFailedFrame(url),
                Services.AppJsonContext.Default.ExternalLinkOpenerFailedFrame)));
        // 外部链接 → 系统默认浏览器（宿主命令路由，见 implemented ADR open-external-links-in-system-browser）
        services.AddSingleton<ICommandRouter>(new Services.ExternalLinkCommandRouter(log: Services.HostLog.Write));
        // dsh 语言变更桥接（desktop.companion.setLocale，ADR host-ui-locale）
        services.AddSingleton<ICommandRouter>(new Services.CompanionLocaleCommandRouter(_uiLocale, log: Services.HostLog.Write));
        // 诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）
        services.AddSingleton<ICommandRouter>(new Services.DesktopDiagnosticsCommandRouter(
            log: Services.HostLog.Write, healthSnapshot: () => _healthMonitor?.Snapshot));
        // 恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
        // 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）
        services.AddSingleton<ICommandRouter>(sp => new Services.RecoveryCommandRouter(
            closeWindow: () => sp.GetRequiredService<IRynWindow>().Close(),
            _closeGate,
            Services.HostLog.Write));
        // 引导重试命令（desktop.bootstrap.retry，ADR online-first-unbundled-runtime）：
        // wwwroot 引导页的重试按钮 → 闸门放行引导循环。gate 实例在 Run 顶部创建，
        // 引导任务与路由共用同一实例
        services.AddSingleton<ICommandRouter>(new Services.BootstrapCommandRouter(
            _bootstrapGate, Services.HostLog.Write));
        // 插件引导决策命令（desktop.preinstall.choose，ADR reference-alignment 批次二）：
        // wwwroot 引导页「插件引导」步的确认装/跳过 → 闸门放行引导任务
        services.AddSingleton<ICommandRouter>(new Services.PreinstallCommandRouter(
            _preinstallGate, Services.HostLog.Write));
        // 开机自启开关（desktop.autostart.getState/set）
        services.AddSingleton<ICommandRouter>(new Services.AutostartCommandRouter(log: Services.HostLog.Write));
        // 关闭最小化到托盘偏好（desktop.closeToTray.getState/set）；available 惰性求值——
        // 服务注册早于托盘初始化，trayReady 由外层闭包稍后赋值
        services.AddSingleton<ICommandRouter>(new Services.Tray.CloseToTrayCommandRouter(
            _closeBehavior, () => _trayReady, log: Services.HostLog.Write));
        // 自更新命令：desktop.update.getState / check / install（dev 门禁下不注册路由，invoke 自然失败）
        if (_updateMachine is not null)
        {
            services.AddSingleton<ICommandRouter>(new Services.Update.DesktopUpdateCommandRouter(_updateMachine, log: Services.HostLog.Write, backgroundToken: () => _supervisorCtsRef?.Token ?? CancellationToken.None));
        }
        // 托盘（批次三，ADR shell-tray-hide-to-tray）：图标+菜单；点击语义经 companion 中继
        // 回 desktop.tray.event 在宿主解析——EmitEvent 是 Ryn 插件内部属性，不在源生成通道
        if (_trayAvailable)
        {
            services.AddRynTray(o =>
            {
                o.IconPath = _iconPath;
                o.Tooltip = "DeepSeek Harness Desktop";
            });
        }
        // 托盘事件路由：窗口动作经委托接 deferred 代理（注册期无需窗口就绪；
        // 委托注入让退出顺序契约可用记序 fake 测试）
        services.AddSingleton<ICommandRouter>(sp =>
        {
            IRynWindow trayWindow = sp.GetRequiredService<IRynWindow>();
            return new Services.Tray.DesktopTrayCommandRouter(
                showWindow: () => RecallAsync(trayWindow),
                closeWindow: () =>
                {
                    // 编排在 supervisorCts 声明后接线，而托盘退出必经托盘菜单的用户交互、
                    // 必然晚于接线，故此处不可能为 null
                    _orderlyQuit!();
                },
                _closeGate,
                _updateMachine,
                Services.HostLog.Write,
                notify: (title, message) =>
                    sp.GetRequiredService<TrayService>().ShowNotification(title, message));
        });
    }

    private void RunBootstrapIfNeeded()
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），后台任务完成
        // 检测/下载/安装/验证状态机，成功后绑定运行时、起 dsh 并导航进主界面；失败推错误态等待
        // 用户重试（desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控
        // （见各自插入点）——依赖序即插入位。
        if (_bootstrapNeeded)
        {
            _bootstrapSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bootstrapCts = new CancellationTokenSource();
            CancellationToken bootCt = _bootstrapCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    (string NodeExe, string DshEntry)? runtime = await RunBootstrapWithRetryAsync(_bootstrapGate, bootCt);
                    if (runtime is null)
                    {
                        Services.HostLog.Write("[bootstrap] 引导未完成（用户放弃或应用退出）");
                        return;
                    }

                    _bundledClosure = runtime;
                    _host.BindRuntime(runtime.Value);

                    // CLI shim 注册（ADR reference-alignment 批次四）：引导完成后运行时已下载就位，
                    // 把 dsh/pnpm 注册进用户 PATH。best-effort（见 RegisterCliShim，内含兜底）。
                    RegisterCliShim(_isDev);

                    // 对齐参照：companion（internal）在 spawn dsh 前静默自愈（batch-1），不出现在
                    // 引导勾选清单（对齐 ensure_internal_plugins）；best-effort：失败只告警不阻断
                    // （缺 companion 不阻塞 dsh 起动，下次启动自愈）。
                    try
                    {
                        await Services.MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                            runtime.Value.NodeExe,
                            runtime.Value.DshEntry,
                            HarnessRuntimeHost.ResolveDshHome(),
                            Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                            Services.HostLog.Write,
                            RunDshPluginAddAsync,
                            bootCt);
                    }
                    catch (Exception ex)
                    {
                        Services.HostLog.Write($"[host] 引导：随包插件安装失败（跳过）：{ex.Message}");
                    }

                    // 首启插件引导（ADR reference-alignment 批次二）：dshmarket（preset）经引导页
                    // chip 确认/跳过 + 日志回流；用户确认后才装（StartAsync 前，与 batch-1 合流）。
                    // 跳过则该次不装（less-bootstrapped，dsh 起动后可从应用内市场/设置自愈补装）。
                    try
                    {
                        await RunPreinstallPhaseAsync(runtime.Value, RunDshPluginAddStreamingAsync, bootCt);
                    }
                    catch (Exception ex)
                    {
                        Services.HostLog.Write($"[host] 插件引导异常跳过：{ex.Message}");
                    }

                    Uri? url = await _host.StartAsync(timeout: TimeSpan.FromSeconds(60), bootCt);
                    if (url is null)
                    {
                        Services.HostLog.Write($"[bootstrap] 引导完成但 dsh 未在时限内给出 URL。stderr 尾巴：\n{string.Join('\n', _host.StderrTail.TakeLast(8))}");
                        return;
                    }

                    Services.HostLog.Write($"[host] runtime = {_host.RuntimeDescription}");
                    Services.HostLog.Write($"[host] dsh web = {url}；从引导页导航进入主界面");
                    _webUrl = url;
                    await _windowAccessor.Current.NavigateAsync(url);
                }
                catch (OperationCanceledException)
                {
                    Services.HostLog.Write("[bootstrap] 引导任务随应用退出取消");
                }
                catch (Exception ex)
                {
                    // 后台引导任务的兜底收口：任何意外异常都不拖垮壳（窗口仍在，可重试或关闭）
                    Services.HostLog.Write($"[bootstrap] 引导任务意外失败：{ex.Message}");
                }
                finally
                {
                    _bootstrapSettled.TrySetResult();
                }
            });
        }
    }

    private void ShowTray()
    {
        // 托盘就绪化（批次三）：装菜单并显示。失败只降级记日志——无托盘环境是合法运行环境；
        // 但下方 hide-to-tray 拦截必须与托盘同 gate：没有召回通道还拦截关窗等于把窗口藏死。
        // 顺序契约：必须先 Show 再 SetMenu——Linux 后端在 Show 前尚未注册 StatusNotifierItem，
        // SetMenu 经 `_item?.` 静默丢弃（v0.3.0 实机图标可见但菜单全无的根因）；macOS 的
        // RebuildMenu 在 status item 未创建时同样丢弃。Windows 两序皆可（菜单右键时才读）。
        if (_trayAvailable)
        {
            try
            {
                TrayService tray = _app.Services.GetRequiredService<TrayService>();
                tray.Show();
                tray.SetMenu(Services.Tray.TrayMenuActions.BuildItems(includeUpdateItem: _updateMachine is not null, _uiLocale));
                _trayReady = true;
                Services.HostLog.Write("[host] 系统托盘已注册");
                // dsh 语言切换 → companion 上报 → locale 变化即重建菜单（ADR host-ui-locale）
                _uiLocale.Changed += () =>
                {
                    try
                    {
                        tray.SetMenu(Services.Tray.TrayMenuActions.BuildItems(includeUpdateItem: _updateMachine is not null, _uiLocale));
                    }
                    catch (Exception ex1)
                    {
                        // 菜单重建失败可容忍：保留旧菜单（文案为上一语言），托盘功能不受损
                        Services.HostLog.Write($"[host] 托盘菜单重建失败（保留旧菜单）：{ex1.Message}");
                    }
                };
            }
            catch (Exception ex)
            {
                Services.HostLog.Write($"[host] 系统托盘初始化失败，关闭窗口将直接退出：{ex.Message}");
            }
        }

        if (_trayReady)
        {
            // IRynWindow 是 deferred 代理：此处窗口尚未创建，Closing 订阅会被缓冲到窗口就绪后挂载。
            // 回调内绝不抛异常——上游对抛异常的 Closing 处理是「放行关窗」，比隐藏更危险。
            IRynWindow trayWindow = _app.Services.GetRequiredService<IRynWindow>();
            trayWindow.Closing += (_, e) =>
            {
                if (!_closeGate.ShouldCancelClose || !_closeBehavior.HideOnClose)
                {
                    // 显式放行通道（托盘退出 / 自更新安装），或用户已选「关闭即退出」
                    return;
                }

                e.Cancel = true;
                _ = HideForTrayAsync(trayWindow);
            };
        }
    }

    private void SetupSupervisor()
    {
        // 原 `using var supervisorCts`：生命周期由 Run 的 finally 释放（本方法赋值）。
        _supervisorCts = new CancellationTokenSource();
        _supervisorCtsRef = _supervisorCts; // 自更新后台任务 token 持有器接线（见顶部声明）
        // 启动期横幅导航门控（ADR shell-firstboot-hardening）：安装任务触发监督器重启后页面会被
        // NavigateAsync 整体替换，横幅必须等这次导航落地再注入，否则与导航竞速被清掉
        // （v0.3.0 实机：18:30:01 注入 vs 18:30:03 导航，旧 home 横幅陪葬）。
        _startupNavigationSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _supervisor = new RuntimeSupervisor(
            _host,
            restartTimeout: TimeSpan.FromSeconds(60),
            showRecovery: () =>
            {
                // 恢复页三件套（ADR diag-masking-and-recovery-page）：失败原因 + stderr 尾部展示 +
                // 导出诊断/退出动作。desktop.* 走 Ryn 层 IPC 不依赖 dsh 存活；数据经 textContent
                // 回填（stderr 是上游不可控输出，绝不 innerHTML 拼接）
                var tail = _host.StderrTail.TakeLast(12).ToList();
                _ = _windowAccessor.Current.EvaluateJavaScriptAsync(
                    Services.RecoveryPageBuilder.BuildScript("运行时进程意外退出，正在自动重启", tail));
                return ValueTask.CompletedTask;
            },
            // 崩溃恢复导航同步刷新 webUrl——健康监视器（有界恢复）靠它作为 reload 靶点；若
            // 崩溃重启用新端口（ADDR child-process-reaping-port-drift 的端口漂移）而 webUrl
            // 仍指向旧 URL，监视器的 reload 会打到已死的旧端口、甚至覆写刚恢复的导航。
            navigate: url =>
            {
                _webUrl = url;
                return _windowAccessor.Current.NavigateAsync(url);
            },
            log: Services.HostLog.Write);
        // 引导期门控：宿主尚无 dsh 进程时 WaitForExitAsync 立即完成，监督器会空转进恢复循环
        // 并用恢复屏覆写引导页——必须等引导落定（成功 spawn 或确认放弃）才进入监视。
        _supervisorTask = Task.Run(async () =>
        {
            if (_bootstrapSettled is not null)
            {
                try
                {
                    await _bootstrapSettled.Task.WaitAsync(_supervisorCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await _supervisor.RunAsync(_supervisorCts.Token);
        });

        // 「导航已到达」信号（ADR ryn-navigation-callbacks）：由 RynNavigationCallbacks 的
        // WebViewNavigated 回调在内容实际提交后触发，取代 RuntimeSupervisor.onNavigated 的
        // 「NavigateAsync 返回即触发」——恢复/横幅门控据此拿到真正的「页面已到达」。
        _app.Services.GetRequiredService<Services.RynNavigationCallbacks>().SetOnNavigated(
            () => _startupNavigationSettled.TrySetResult());

        // 有序退出编排接线（ADR child-process-reaping-port-drift）：运行时回收先于关窗，
        // 不依赖 GTK loop 对隐藏态窗口 close 的行为；8s 看门狗把静默滞留变成确定性终结。
        // 正常路径 Run 返回后 Run 尾部重复 Cancel/Stop/Release 均幂等，双路径收敛。
        IRynWindow quitWindow = _app.Services.GetRequiredService<IRynWindow>();
        _orderlyQuit = () =>
        {
            Services.HostLog.Write("[tray] 有序退出：回收运行时后关闭窗口");
            Services.ExitOrchestration.OrderlyQuit(
                () => _supervisorCts.Cancel(),
                _host.Stop,
                () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), _marker.Token),
                () => _instanceListener?.Dispose(),
                quitWindow.Close,
                StartQuitWatchdog);
        };

        // 自更新兜底退出收割器接线（ADR self-update-exit-reaps-dsh-child）：经回收三件套单点
        // （ExitOrchestration.ReapRuntime），不带关窗与看门狗——
        // 自更新路径由 pkexec 脚本接管进程接力，关窗已由 install 委托直退，此处只保证 dsh 不泄漏。
        // 与 orderlyQuit 同款「先回收再退」；StartExitFallback 触发时 supervisorCts/host/marker 均已就绪。
        _updateExitReaper = () =>
        {
            Services.HostLog.Write("[update] 兜底回收：cancel 监督器 + 整树击杀 dsh + 释放 marker");
            Services.ExitOrchestration.ReapRuntime(
                () => _supervisorCts.Cancel(),
                _host.Stop,
                () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), _marker.Token));
        };
    }

    private void SetupHealthMonitor()
    {
        // 页面健康观测 + 有界恢复（ADR page-health-monitor / reference-alignment 批次五）：
        // 宿主只读探针轮询，不注入不依赖 companion——「dsh 在跑但页面空白」类事故（历史三起全靠
        // 人肉发现）从此有自动留痕；连续 Dead 达阈值后在预算内触发一次有界 reload，耗尽转观测-only，
        // 成功恢复复位预算（防误报引发无限重载循环，对齐参照 plugin_boot.rs 的有界刷新门控）。
        // 首拍延迟 10s 避开启动空窗，探针异常按 Unknown 续跑。reload 委托捕获 webUrl（字段，
        // 初始/引导完成/崩溃恢复导航三处都会刷新，见上文与 RuntimeSupervisor 的 navigate），
        // 恒为当前 dsh web 靶点；webUrl 只有当引导未落定（dsh 未起）才为空，而该窗口页面是
        // wwwroot 引导页（有内容 → Alive），不会进入 Dead 恢复分支——reload 委托的空态只是防御性兜底。
        _healthMonitor = new Services.PageHealthMonitor(
            _windowAccessor,
            Services.HostLog.Write,
            reload: ct => _webUrl is null
                ? ValueTask.CompletedTask
                : _windowAccessor.Current.NavigateAsync(_webUrl, ct));
        _ = _healthMonitor.RunAsync(TimeSpan.FromSeconds(10), _supervisorCts.Token);
    }

    private void StartUpdateCheck()
    {
        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        if (_updateMachine is not null)
        {
            // 就绪横幅（批次三）：ready 到达一次性提示（订阅在窗口句柄就绪后建立，去重防重试期反复弹）
            _updateMachine.Subscribe(state =>
            {
                if (state.Status == Services.Update.UpdateStatus.Ready &&
                    state.Version is not null && !_readyNotified)
                {
                    _readyNotified = true;
                    _ = ShowBannerWhenReady(
                        _windowAccessor,
                        Services.Update.UpdateBanner.ReadyScript(state.Version, _uiLocale),
                        _supervisorCts.Token);
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await _updateMachine.StartAsync(_supervisorCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Services.HostLog.Write($"[update] start 失败：{ex.Message}");
                }
            });
        }
    }

    private void SharedHomeBannerTask()
    {
        // 共享 home 切换的启动期告知（ADR shared-home-desktop-profile）：版本底线检查 + 旧 home 一次性提示。
        // 随包插件现于 spawn dsh 前安装（不再「启动后装 → 覆写页面并重启运行时」），横幅无需等安装收尾，
        // 只需等首启引导落定——版本探针用的 bundledClosure 在引导完成时被赋值，提前跑会探到空。
        _ = Task.Run(async () =>
        {
            try
            {
                if (_bootstrapSettled is not null)
                {
                    await _bootstrapSettled.Task.WaitAsync(TimeSpan.FromSeconds(120), _supervisorCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                // 引导链路迟迟未定也照常告知：横幅是增强信息，不能因异常路径永久缺席
            }

            string home = HarnessRuntimeHost.ResolveDshHome();
            string? detected = await Services.RuntimeVersionGate.ProbeAsync(_bundledClosure, _supervisorCts.Token);
            if (detected is not null)
            {
                Services.HostLog.Write($"[host] dsh 版本 {detected}（底线 {Services.RuntimeVersionGate.MinimumVersion}）");
                if (Services.RuntimeVersionGate.IsBelowFloor(detected))
                {
                    Services.HostLog.Write($"[host] 警告：dsh {detected} 低于支持底线 {Services.RuntimeVersionGate.MinimumVersion}，已提示用户");
                    await ShowBannerWhenReady(_windowAccessor, Services.RuntimeVersionGate.BelowFloorBannerScript(detected, _uiLocale), _supervisorCts.Token);
                }
            }
            else
            {
                Services.HostLog.Write("[host] dsh 版本探测失败，跳过底线检查");
            }

            // 旧 home 留痕仅进日志（界面横幅已按用户拍板去除，ADR companion-settings-consolidation）；
            // 指回旧目录时不记「改用新目录」——自相矛盾且无信息量
            if (LegacyHomeNotice.IsPresent() && !PathsEqual(home, LegacyHomeNotice.LegacyPrivateHome))
            {
                Services.HostLog.Write($"[host] 检测到旧版桌面数据目录 {LegacyHomeNotice.LegacyPrivateHome}；新版使用 {home}（未迁移）");
            }

            // 上轮非受控退出：提示但不暗示应用故障（用户杀进程也属此类），引导导出诊断
            if (_previousRunUnclean)
            {
                await ShowBannerWhenReady(_windowAccessor, RunMarker.UncleanBannerScript(_uiLocale), _supervisorCts.Token);
            }
        });
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
