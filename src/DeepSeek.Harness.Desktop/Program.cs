using System.Runtime.InteropServices;
using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Callbacks;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop;

/// <summary>DeepSeek Harness Desktop 入口：Ryn 桌面壳 + 托管 dsh 运行时 + 崩溃监督。</summary>
public static class Program
{
    /// <summary>引导进度帧（wwwroot 引导页监听 <c>dsh-desktop-bootstrap</c> CustomEvent 渲染）。</summary>
    internal sealed record BootstrapStateFrame(string Step, string Message, bool Failed);

    /// <summary>
    /// 壳启动流程：托管 dsh web（OS 分配端口）→ 解析 `dsh web:` URL → Ryn WebView 加载；
    /// 后台监督 dsh 子进程——崩溃只重启子进程并导航新 URL（不重启桌面进程）；dsh 起不来时降级加载本地 wwwroot。
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // 无 UI 兜底诊断导出（ADR shell-observability-diagnostics）：先于一切启动逻辑——
        // 不 spawn dsh、不开窗、不做 dev 隔离，覆盖「闪退进不了界面」的取证场景。
        // CLI 形态下 stdout 可见；失败以非零退出码 fail loud（脚本可判定）。
        if (Array.IndexOf(args, "--export-diagnostics") >= 0)
        {
            try
            {
                var result = DiagnosticsExporter.ExportWithFallback(
                    HarnessRuntimeHost.ResolveDshHome(),
                    Services.Update.AppVersion.Current());
                // CLI 形态下 stdout 可见；经 HostLog 双写让桌面形态的同一动作也落 host.log
                Services.HostLog.Write($"[host] 诊断包已导出：{result.ZipPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[host] 诊断包导出失败：{ex.Message}");
                return 1;
            }
        }

        // 开发运行时完全隔离（ADR dev-runtime-isolation）：ApplicationId 加 .dev 后缀避开
        // GTK 同 id 单实例互斥（与已装正式版可同时开窗）；DSH_HOME 未显式覆盖时自动指向
        // 仓库 .cache/dev-home，杜绝与正式版共享 profile 的串扰。
        // dev 判定只认显式环境标记（DSH_DESKTOP_RUNTIME_DIR / DSH_DESKTOP_DEV=1）——绝不以
        // 捆绑闭包存在性探测（online-first 后打包新装同样没有闭包，探测会误判全部新装用户，
        // ADR online-first-unbundled-runtime 批次二收口 shared-home 挂账）。
        var updateRuntimeDir = RuntimeLocator.ResolveRuntimeDirectory();
        var bundledClosure = RuntimeLocator.TryLocateBundled(updateRuntimeDir);

        // 首启引导判定（ADR online-first-unbundled-runtime）：捆绑运行时缺失且 PATH dsh 不可用
        // 时进入引导（下载钉版 Node + registry 安装 dsh）。检测是毫秒级只读探测，同步执行。
        var bootstrapNeeded = false;
        if (bundledClosure is null)
        {
            var pathVersion = Services.RuntimeVersionGate.ProbeAsync(null, CancellationToken.None)
                .GetAwaiter().GetResult();
            bootstrapNeeded = pathVersion is null;
            Services.HostLog.Write(bootstrapNeeded
                ? "[bootstrap] 捆绑运行时与 PATH dsh 均未检出，进入首启引导"
                : $"[bootstrap] PATH dsh 可用（{pathVersion}），跳过首启引导");
        }

        // 引导共享状态（声明提前：命令路由注册、监督器门控、插件安装门控、后台引导任务共用）。
        // gate 供引导页 desktop.bootstrap.retry 命令放行重试循环；settled 在引导终态（成功/失败放弃/
        // 取消）置位——监督器与插件安装都等它，防引导期误拉 dsh 或误装插件。
        var bootstrapGate = new Services.RuntimeBootstrapGate();
        TaskCompletionSource? bootstrapSettled = null;
        CancellationTokenSource? bootstrapCts = null;
        var devRuntimeDir = Environment.GetEnvironmentVariable(DevEnvironment.RuntimeDirEnv);
        var devFlag = Environment.GetEnvironmentVariable(DevEnvironment.DevFlagEnv);
        var isDev = DevEnvironment.IsDevRuntime(devRuntimeDir, devFlag);
        var devAutoIsolated = false;
        if (isDev && Environment.GetEnvironmentVariable(DevEnvironment.HomeOverrideEnv) is null)
        {
            var devHome = DevEnvironment.DeriveDefaultDevHome(devRuntimeDir, AppContext.BaseDirectory);
            if (devHome is not null)
            {
                Environment.SetEnvironmentVariable(DevEnvironment.HomeOverrideEnv, devHome);
                devAutoIsolated = true;
                Services.HostLog.Write($"[host] 开发运行时：DSH_HOME 隔离到 {devHome}；ApplicationId 带 .dev 后缀，可与正式版并存");
            }
        }
        else if (!isDev && bundledClosure is null &&
                 DevEnvironment.DeriveDefaultDevHome(null, AppContext.BaseDirectory) is not null)
        {
            // dev 判定改显式标记后的唯一残留风险（R2 评审）：贡献者在仓库内跑却忘带
            // DSH_DESKTOP_DEV=1 —— 判定按设计走打包产品语义，但值得一条 host.log 诊断指路
            Services.HostLog.Write("[host] 疑似仓库内开发运行但未设 DSH_DESKTOP_DEV=1：按打包产品处理（共享真实 home，无 dev 隔离）");
        }

        // hide-to-tray 唤回的最大化保持样本（ADR tray-recall-maximize-and-check-feedback）：
        // 隐藏前采样（1=最大化 / 0=非 / -1=未知），唤回路径按判据消费后在 finally 无条件清零。
        // 声明置于最前：launcher 激活回调与托盘召回共用同一份样本语义，激活唤起同样消费，
        // 防残留样本让下一次托盘点击把用户手动还原的窗口误最大化。
        var maximizedAtHide = -1;

        // 单实例仲裁（ADR single-instance-launcher-activation）：Wayland 下 GTK 按 ApplicationId
        // 的互斥不生效（v0.3.7 实机：launcher 二启产生完整新实例），自建 UDS 仲裁先于一切
        // GTK/运行时初始化执行。二启通知主实例显示主窗后立即退出，绝不重复拉起运行时。
        // 回调经 updateWindow 延迟代理（Build 后赋值）；窗口代理就绪前 Current 抛
        // InvalidOperationException，由回调 catch 记日志后放弃——激活请求丢失可接受，
        // 首启本就会自动开窗。updateWindow 与自更新栈共用同一延迟句柄，
        // 声明必须先于本回调（作用域捕获）。
        CurrentWindowAccessor? updateWindow = null;
        // 自更新兜底退出收割器（ADR self-update-exit-reaps-dsh-child）：install 委托（195 行）调用
        // StartExitFallback 时引用不到后声明的 supervisorCts（446 行），故经持有器延迟接线——
        // 与 251 行 orderlyQuit 同款模式；在 supervisorCts 声明后赋值为「cancel 监督器 + 整树击杀 dsh + 释放 marker」。
        Action? updateExitReaper = null;
        // 自更新后台长任务（check 下载/install 兜底）的取消令牌来源同款延迟接线
        CancellationTokenSource? supervisorCtsRef = null;
        // 宿主 UI 语言单点（ADR host-ui-locale）：companion 上报 dsh locale，托盘/横幅据此出双语
        var uiLocale = new Services.UiLocale();
        PrimaryListener? instanceListener = null;
        var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var instanceSocketPath = OperatingSystem.IsWindows()
            ? null // Windows 无验证环境不启用互斥，行为维持现状（ADR 平台边界）
            : LauncherActivation.SocketPath(
                xdgRuntimeDir is { Length: > 0 } ? xdgRuntimeDir : Path.GetTempPath(),
                "deepseek-harness-desktop" +
                (xdgRuntimeDir is { Length: > 0 } ? string.Empty : LauncherActivation.FallbackUidSuffix()),
                isDev);
        if (instanceSocketPath is not null)
        {
            if (!LauncherActivation.TryBindPrimary(
                    instanceSocketPath,
                    onShowRequested: async () =>
                    {
                        var accessor = updateWindow;
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
                            Volatile.Write(ref maximizedAtHide, -1);
                        }
                    },
                    Services.HostLog.Write,
                    out instanceListener))
            {
                var notified = LauncherActivation.NotifyPrimary(instanceSocketPath, TimeSpan.FromSeconds(2));
                Services.HostLog.Write(
                    $"[host] 已有主实例在运行（launcher 二次启动）：通知显示主窗{(notified ? "成功" : "未达（主实例可能正忙）")}，本次启动退出");
                return 0;
            }
        }

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
            var reconciled = DesktopProfileBootstrap.ReconcileProfile(HarnessRuntimeHost.ResolveDshHome(), Services.HostLog.Write);
            if (reconciled > 0)
            {
                Services.HostLog.Write($"[host] 桌面 profile reconcile：移除 {reconciled} 个不可解析插件引用");
            }
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[host] profiles/desktop 初始化失败（dsh 可能拒启，详见后续降级链路）：{ex.Message}");
        }

        using var host = new HarnessRuntimeHost(bundledClosure, Services.HostLog.Write);

        // 崩溃取证 marker（ADR shell-observability-diagnostics）：遗留即判定上轮非受控退出；
        // 正常退出路径在 Main 尾部按 token 清除
        var marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());
        var previousRunUnclean = marker.PreviousRunUnclean;
        if (previousRunUnclean)
        {
            Services.HostLog.Write("[host] 检测到上轮未正常退出的标记；如频繁出现请在设置页导出诊断信息");
        }
        // 对齐参照（dsh-tauri-desk launch.rs）：随包插件（companion）在 spawn dsh 前安装，绝不
        // 「启动后 3s 装 → 重启」。此路径为 bundled/PATH-dsh 且非引导；引导路径在后台任务内、
        // BindRuntime 后同样 spawn 前安装。dev 显式覆盖共享 home 时跳过（防串扰）。
        if (!bootstrapNeeded && bundledClosure is not null && !(isDev && !devAutoIsolated))
        {
            try
            {
                Services.MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                    bundledClosure.Value.NodeExe,
                    bundledClosure.Value.DshEntry,
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

        var webUrl = bootstrapNeeded
            ? null
            : host.StartAsync(timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (!bootstrapNeeded)
        {
            Services.HostLog.Write($"[host] runtime = {host.RuntimeDescription}");
            if (webUrl is not null)
            {
                Services.HostLog.Write($"[host] dsh web = {webUrl}");
            }
            else
            {
                Services.HostLog.Write($"[host] dsh 未在时限内给出 URL；降级加载 wwwroot。stderr 尾巴：\n{string.Join('\n', host.StderrTail.TakeLast(8))}");
            }
        }

        // hide-to-tray 关窗闸门（ADR shell-tray-hide-to-tray）：托盘「退出」与自更新安装路径
        // 先批准再 Close。用户普通关窗是否转隐藏由 closeBehavior 偏好裁决（默认 true 保持
        // 历史行为）；托盘未就绪时拦截不生效（关窗直退）。
        var closeGate = new Services.Tray.CloseGate();
        var closeBehavior = new Services.Tray.CloseBehaviorPreference(
            Path.Combine(HarnessRuntimeHost.ResolveDshHome(), Services.Tray.CloseBehaviorPreference.FileName));

        // 自更新栈（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）：
        // 状态机纯逻辑可单测，检查/下载/安装全部委托注入；状态经 CustomEvent 推给插件 UI。
        // dev 运行时不装载（除非 DSH_DESKTOP_UPDATE_FORCE=1 显式开启验证）：dev 构建版本同 csproj，
        // 一旦比对出新 release，点击会把官方包装进系统后按 Environment.ProcessPath 拉起**旧 dev 二进制**，
        // 版本不变、ready 记录不清，形成循环（审核加固，见 ADR self-update-review-hardening）。
        var updateEnabled = Services.Update.UpdateOptions.IsEnabledFor(
            isDev,
            Environment.GetEnvironmentVariable(Services.Update.UpdateOptions.ForceDevEnv));
        Services.Update.UpdateStateMachine? updateMachine = null;
        var readyNotified = false;
        if (updateEnabled)
        {
            var updateOptions = Services.Update.UpdateOptions.Load(AppContext.BaseDirectory);
            var updateHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var updatesDir = Path.Combine(HarnessRuntimeHost.ResolveDshHome(), updateOptions.UpdatesDirName);
            var updatePkgKind = Services.Update.UpdatePlatform.DetectCurrentPackageKind();
            updateMachine = new Services.Update.UpdateStateMachine(
                currentVersion: Services.Update.AppVersion.Current(),
                check: ct => new Services.Update.ReleaseMetaClient(updateHttp, updateOptions, Services.HostLog.Write).FetchLatestAsync(UpdateRid(), updatePkgKind, ct),
                download: (meta, ct) => new Services.Update.InstallerDownloader(updateHttp, Services.HostLog.Write).DownloadAsync(
                    meta, updatesDir, TimeSpan.FromMinutes(updateOptions.DownloadTimeoutMinutes), ct),
                install: async (assetPath, version, ct) =>
                {
                    // 安装时点自 release SHA256SUMS 复取期望哈希（HTTPS 直达仓库，用户空间改写不了）：
                    // root 侧装前复验对照它——落盘的任何哈希都可被同权限改写，唯有 release 侧值是锚点。
                    // 离线时此处抛出拒装（状态机回 ready），不无复核安装。
                    var expectedSha = await new Services.Update.InstallerDownloader(updateHttp, Services.HostLog.Write)
                        .FetchSha256Async(updateOptions.Repository, version, Path.GetFileName(assetPath), ct);
                    // 授权通过（LaunchAsync 观察窗口内未取消）后：主动关闭窗口让进程退出，
                    // 安装脚本的等待环随即放行 rpm/dpkg 并拉起新版。缺这步脚本会死等本进程。
                    await Services.Update.UpdateInstaller.LaunchAsync(assetPath, updatesDir, expectedSha, ct, log: Services.HostLog.Write);
                    Services.HostLog.Write("[update] 授权通过，关闭应用以继续安装…");
                    // 安装路径与托盘退出共用闸门：先批准，Close 才不会被 hide-to-tray 拦截转成隐藏
                    closeGate.ApproveExit();
                    try
                    {
                        updateWindow?.Current?.Close();
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
                    PushUpdateState(updateWindow, state);
                });
            Services.HostLog.Write($"[host] 自更新：当前版本 {Services.Update.AppVersion.Current()}，RID {UpdateRid()}，包类型 {updatePkgKind ?? "(n/a)"}，目录 {updatesDir}，feed 超时 {updateOptions.FeedTimeoutSeconds}s 下载超时 {updateOptions.DownloadTimeoutMinutes}m");
        }
        else
        {
            Services.HostLog.Write("[host] 自更新：dev 运行时不装载（DSH_DESKTOP_UPDATE_FORCE=1 可显式开启）");
        }

        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 trayReady）
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        var trayAvailable = File.Exists(iconPath);

        // hide-to-tray 唤回的最大化保持（ADR tray-recall-maximize-and-check-feedback）：
        // 上游 ShowAsync 不保留最大化（0.3.2 实机复验仍复现）。原生 IRynWindow.IsMaximized
        // 作隐藏前采样（Ryn 0.30.3 起暴露、本仓自 0.30.4 消费；MAXIMIZE 事件镜像 + 启动期
        // 原生同步）；
        // Ryn 0.30.5 起动作面统一为幂等 SetMaximized(bool)，调用点不再读镜像（该镜像
        // 在 Fedora Wayland 实证不可信）。两拍动作：①Linux 隐藏态预置——ShowAsync 前
        // 对未映射窗口设最大化，map 即最大化，消除「先默认尺寸再最大化」的首唤闪变；
        // ②唤回后兜底确认——显示 300ms 后再设一次，幂等保证预置已生效时为原生 no-op。
        // 样本本体声明在文件最前（launcher 激活回调共用消费语义）。
        // 托盘就绪标志：先于服务注册声明（closeToTray 路由的 available 委托引用），
        // 托盘初始化后赋值；初始化失败保持 false（关窗直退、偏好开关呈不可用）。
        var trayReady = false;
        // 有序退出编排（ADR child-process-reaping-port-drift）：托盘退出不再裸调窗口 Close——
        // GTK loop 对隐藏态窗口的 close 可能不退出主循环（v0.3.7 实机滞留实证），运行时回收
        // 必须先于 Close 确定性执行。注册期早于 supervisorCts/marker 声明，故经持有器延迟接线。
        Action? orderlyQuit = null;
        // 页面健康监视器：先于服务注册声明（诊断路由的快照委托引用），监督器接线处赋值
        Services.PageHealthMonitor? healthMonitor = null;

        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                if (webUrl is not null)
                {
                    // dsh web UI（loopback；完整运行时随应用内置后仍是此路径）
                    opts.Url = webUrl;
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
                    "io.github.ZK-Andy.dotnet-deepseek-harness-desktop", isDev);
                if (File.Exists(iconPath))
                {
                    opts.IconPath = iconPath;
                }
                else
                {
                    Services.HostLog.Write($"[host] icon 缺失：{iconPath}");
                }

                Services.HostLog.Write($"[host] Ryn opts: Url={(webUrl is not null ? webUrl.ToString() : "null")} ApplicationId={opts.ApplicationId} Icon={(File.Exists(iconPath) ? iconPath : "missing")}");
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(services =>
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
                    currentOrigin: webUrl?.GetLeftPart(UriPartial.Authority),
                    // 外部链接打开失败 → 推事件给页面，companion 渲染 toast（R2 N2）。EmitEvent 走
                    // deferred IRynWebView（窗口就绪后转发），在导航回调触发时页面必然已加载。
                    notifyLinkFail: url => sp.GetRequiredService<IRynWebView>().EmitEvent(
                        "desktop.externalLinkOpenerFailed",
                        new Services.ExternalLinkOpenerFailedFrame(url),
                        Services.AppJsonContext.Default.ExternalLinkOpenerFailedFrame)));
                // 外部链接 → 系统默认浏览器（宿主命令路由，见 implemented ADR open-external-links-in-system-browser）
                services.AddSingleton<ICommandRouter>(new Services.ExternalLinkCommandRouter(log: Services.HostLog.Write));
                // dsh 语言变更桥接（desktop.companion.setLocale，ADR host-ui-locale）
                services.AddSingleton<ICommandRouter>(new Services.CompanionLocaleCommandRouter(uiLocale, log: Services.HostLog.Write));
                // 诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）
                services.AddSingleton<ICommandRouter>(new Services.DesktopDiagnosticsCommandRouter(
                    log: Services.HostLog.Write, healthSnapshot: () => healthMonitor?.Snapshot));
                // 恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
                // 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）
                services.AddSingleton<ICommandRouter>(sp => new Services.RecoveryCommandRouter(
                    closeWindow: () => sp.GetRequiredService<IRynWindow>().Close(),
                    closeGate,
                    Services.HostLog.Write));
                // 引导重试命令（desktop.bootstrap.retry，ADR online-first-unbundled-runtime）：
                // wwwroot 引导页的重试按钮 → 闸门放行引导循环。gate 实例在 Main 顶部创建，
                // 引导任务与路由共用同一实例
                services.AddSingleton<ICommandRouter>(new Services.BootstrapCommandRouter(
                    bootstrapGate, Services.HostLog.Write));
                // 开机自启开关（desktop.autostart.getState/set）
                services.AddSingleton<ICommandRouter>(new Services.AutostartCommandRouter(log: Services.HostLog.Write));
                // 关闭最小化到托盘偏好（desktop.closeToTray.getState/set）；available 惰性求值——
                // 服务注册早于托盘初始化，trayReady 由外层闭包稍后赋值
                services.AddSingleton<ICommandRouter>(new Services.Tray.CloseToTrayCommandRouter(
                    closeBehavior, () => trayReady, log: Services.HostLog.Write));
                // 自更新命令：desktop.update.getState / check / install（dev 门禁下不注册路由，invoke 自然失败）
                if (updateMachine is not null)
                {
                    services.AddSingleton<ICommandRouter>(new Services.Update.DesktopUpdateCommandRouter(updateMachine, log: Services.HostLog.Write, backgroundToken: () => supervisorCtsRef?.Token ?? CancellationToken.None));
                }
                // 托盘（批次三，ADR shell-tray-hide-to-tray）：图标+菜单；点击语义经 companion 中继
                // 回 desktop.tray.event 在宿主解析——EmitEvent 是 Ryn 插件内部属性，不在源生成通道
                if (trayAvailable)
                {
                    services.AddRynTray(o =>
                    {
                        o.IconPath = iconPath;
                        o.Tooltip = "DeepSeek Harness Desktop";
                    });
                }
                // 托盘事件路由：窗口动作经委托接 deferred 代理（注册期无需窗口就绪；
                // 委托注入让退出顺序契约可用记序 fake 测试）
                services.AddSingleton<ICommandRouter>(sp =>
                {
                    var trayWindow = sp.GetRequiredService<IRynWindow>();
                    async Task RecallAsync()
                    {
                        // 样本入口即取本地快照，finally 无条件消费——若下方任一 await 抛出，
                        // 残留样本会让下一次托盘点击把用户手动还原的窗口误最大化。
                        var sample = Volatile.Read(ref maximizedAtHide);
                        try
                        {
                            // Linux 隐藏态预置（v0.3.6 实机反馈的首唤闪变在此消除）：对未映射窗口
                            // 显式设最大化——幂等、不读事件镜像（该机实证镜像不可信，旧镜像门控
                            // 让预置永不触发=闪变复发）。GTK 把未映射窗口的 maximize 记为初始态，
                            // map 时直接以最大化呈现。deferred 代理在窗口未就绪时可能抛出：放弃
                            // 预置，兜底确认仍在。
                            try
                            {
                                if (OperatingSystem.IsLinux() && Services.Tray.TrayRecallMaximize.ShouldEnsure(sample))
                                {
                                    trayWindow.SetMaximized(true);
                                    Services.HostLog.Write("[tray] 唤回：隐藏态已预置最大化");
                                }
                            }
                            catch (Exception ex)
                            {
                                Services.HostLog.Write($"[tray] 隐藏态预置最大化失败：{ex.Message}");
                            }

                            await trayWindow.ShowAsync().AsTask();
                            // 兜底确认（各平台统一的第二拍）：显示后显式设最大化。目标态幂等
                            // ——预置已生效时这里是原生层 no-op，无需任何跳过守卫；延迟一拍沿用
                            // 既有节奏（亚秒级等待不接监督器取消令牌）。
                            if (Services.Tray.TrayRecallMaximize.ShouldEnsure(sample))
                            {
                                await Task.Delay(300);
                                trayWindow.SetMaximized(true);
                                // 兜底拍留痕：预置拍已打日志，此拍若不落痕，「两拍只走了一拍」无从判别
                                Services.HostLog.Write("[tray] 唤回：显示后兜底确认已发");
                            }
                        }
                        finally
                        {
                            Volatile.Write(ref maximizedAtHide, -1);
                        }
                    }

                    return new Services.Tray.DesktopTrayCommandRouter(
                        RecallAsync,
                        closeWindow: () =>
                        {
                            // 编排在 supervisorCts 声明后接线，而托盘退出必经托盘菜单的用户交互、
                            // 必然晚于接线，故此处不可能为 null
                            orderlyQuit!();
                        },
                        closeGate,
                        updateMachine,
                        Services.HostLog.Write,
                        notify: (title, message) =>
                            sp.GetRequiredService<TrayService>().ShowNotification(title, message));
                });
            })
            .Build();

        var windowAccessor = app.Services.GetRequiredService<CurrentWindowAccessor>();
        updateWindow = windowAccessor;

        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），后台任务完成
        // 检测/下载/安装/验证状态机，成功后绑定运行时、起 dsh 并导航进主界面；失败推错误态等待
        // 用户重试（desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控
        // （见各自插入点）——依赖序即插入位。
        if (bootstrapNeeded)
        {
            bootstrapSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bootstrapCts = new CancellationTokenSource();
            var bootCt = bootstrapCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var runtime = await RunBootstrapWithRetryAsync(bootstrapGate, bootCt);
                    if (runtime is null)
                    {
                        Services.HostLog.Write("[bootstrap] 引导未完成（用户放弃或应用退出）");
                        return;
                    }

                    bundledClosure = runtime;
                    host.BindRuntime(runtime.Value);

                    // 首启引导经 registry 安装市场（ADR online-first-unbundled-runtime 批次三：
                    // dshmarket 不再随包/种子，改由引导在 spawn dsh 前注册表安装——插件与核心一次就位，
                    // 不再「后台 3s 装 → 重启」。显式 @latest 对既存本地 seed 同样强制改写为 registry 形态
                    // （承接 bundled-plugin-registry-normalization 的归化语义）。best-effort：失败只告警不阻断
                    // 首启——市场缺失不阻塞 dsh 起动，失败留痕下次引导（或用户手动装市场）自愈。
                    try
                    {
                        await Services.MarketInstallHelper.EnsureMarketFromRegistryAsync(
                            runtime.Value.NodeExe,
                            runtime.Value.DshEntry,
                            HarnessRuntimeHost.ResolveDshHome(),
                            Services.HostLog.Write,
                            RunDshPluginAddAsync,
                            bootCt);
                    }
                    catch (Exception ex)
                    {
                        Services.HostLog.Write($"[host] 市场安装异常跳过：{ex.Message}");
                    }

                    // 对齐参照：companion（internal）与 market 同为 spawn dsh 前、BindRuntime 后就位，
                    // 杜绝「启动后 3s 装 → 重启 dsh」的首启二次启动。best-effort：失败只告警不阻断。
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

                    var url = await host.StartAsync(timeout: TimeSpan.FromSeconds(60), bootCt);
                    if (url is null)
                    {
                        Services.HostLog.Write($"[bootstrap] 引导完成但 dsh 未在时限内给出 URL。stderr 尾巴：\n{string.Join('\n', host.StderrTail.TakeLast(8))}");
                        return;
                    }

                    Services.HostLog.Write($"[host] runtime = {host.RuntimeDescription}");
                    Services.HostLog.Write($"[host] dsh web = {url}；从引导页导航进入主界面");
                    webUrl = url;
                    await windowAccessor.Current.NavigateAsync(url);
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
                    bootstrapSettled.TrySetResult();
                }
            });
        }

        // 托盘就绪化（批次三）：装菜单并显示。失败只降级记日志——无托盘环境是合法运行环境；
        // 但下方 hide-to-tray 拦截必须与托盘同 gate：没有召回通道还拦截关窗等于把窗口藏死。
        // 顺序契约：必须先 Show 再 SetMenu——Linux 后端在 Show 前尚未注册 StatusNotifierItem，
        // SetMenu 经 `_item?.` 静默丢弃（v0.3.0 实机图标可见但菜单全无的根因）；macOS 的
        // RebuildMenu 在 status item 未创建时同样丢弃。Windows 两序皆可（菜单右键时才读）。
        if (trayAvailable)
        {
            try
            {
                var tray = app.Services.GetRequiredService<TrayService>();
                tray.Show();
                tray.SetMenu(Services.Tray.TrayMenuActions.BuildItems(includeUpdateItem: updateMachine is not null, uiLocale));
                trayReady = true;
                Services.HostLog.Write("[host] 系统托盘已注册");
                // dsh 语言切换 → companion 上报 → locale 变化即重建菜单（ADR host-ui-locale）
                uiLocale.Changed += () =>
                {
                    try
                    {
                        tray.SetMenu(Services.Tray.TrayMenuActions.BuildItems(includeUpdateItem: updateMachine is not null, uiLocale));
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

        if (trayReady)
        {
            // IRynWindow 是 deferred 代理：此处窗口尚未创建，Closing 订阅会被缓冲到窗口就绪后挂载。
            // 回调内绝不抛异常——上游对抛异常的 Closing 处理是「放行关窗」，比隐藏更危险。
            var trayWindow = app.Services.GetRequiredService<IRynWindow>();
            trayWindow.Closing += (_, e) =>
            {
                if (!closeGate.ShouldCancelClose || !closeBehavior.HideOnClose)
                {
                    // 显式放行通道（托盘退出 / 自更新安装），或用户已选「关闭即退出」
                    return;
                }

                e.Cancel = true;
                _ = HideForTrayAsync(trayWindow);
            };
        }

        using var supervisorCts = new CancellationTokenSource();
        supervisorCtsRef = supervisorCts; // 自更新后台任务 token 持有器接线（见顶部声明）
        // 启动期横幅导航门控（ADR shell-firstboot-hardening）：安装任务触发监督器重启后页面会被
        // NavigateAsync 整体替换，横幅必须等这次导航落地再注入，否则与导航竞速被清掉
        // （v0.3.0 实机：18:30:01 注入 vs 18:30:03 导航，旧 home 横幅陪葬）。
        var startupNavigationSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new RuntimeSupervisor(
            host,
            restartTimeout: TimeSpan.FromSeconds(60),
            showRecovery: () =>
            {
                // 恢复页三件套（ADR diag-masking-and-recovery-page）：失败原因 + stderr 尾部展示 +
                // 导出诊断/退出动作。desktop.* 走 Ryn 层 IPC 不依赖 dsh 存活；数据经 textContent
                // 回填（stderr 是上游不可控输出，绝不 innerHTML 拼接）
                var tail = host.StderrTail.TakeLast(12).ToList();
                _ = windowAccessor.Current.EvaluateJavaScriptAsync(
                    Services.RecoveryPageBuilder.BuildScript("运行时进程意外退出，正在自动重启", tail));
                return ValueTask.CompletedTask;
            },
            navigate: url => windowAccessor.Current.NavigateAsync(url),
            log: Services.HostLog.Write);
        // 引导期门控：宿主尚无 dsh 进程时 WaitForExitAsync 立即完成，监督器会空转进恢复循环
        // 并用恢复屏覆写引导页——必须等引导落定（成功 spawn 或确认放弃）才进入监视。
        var supervisorTask = Task.Run(async () =>
        {
            if (bootstrapSettled is not null)
            {
                try
                {
                    await bootstrapSettled.Task.WaitAsync(supervisorCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await supervisor.RunAsync(supervisorCts.Token);
        });

        // 「导航已到达」信号（ADR ryn-navigation-callbacks）：由 RynNavigationCallbacks 的
        // WebViewNavigated 回调在内容实际提交后触发，取代 RuntimeSupervisor.onNavigated 的
        // 「NavigateAsync 返回即触发」——恢复/横幅门控据此拿到真正的「页面已到达」。
        app.Services.GetRequiredService<Services.RynNavigationCallbacks>().SetOnNavigated(
            () => startupNavigationSettled.TrySetResult());

        // 有序退出编排接线（ADR child-process-reaping-port-drift）：运行时回收先于关窗，
        // 不依赖 GTK loop 对隐藏态窗口 close 的行为；8s 看门狗把静默滞留变成确定性终结。
        // 正常路径 Run 返回后 Main 尾部重复 Cancel/Stop/Release 均幂等，双路径收敛。
        var quitWindow = app.Services.GetRequiredService<IRynWindow>();
        orderlyQuit = () =>
        {
            Services.HostLog.Write("[tray] 有序退出：回收运行时后关闭窗口");
            Services.ExitOrchestration.OrderlyQuit(
                () => supervisorCts.Cancel(),
                host.Stop,
                () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), marker.Token),
                () => instanceListener?.Dispose(),
                quitWindow.Close,
                StartQuitWatchdog);
        };

        /// <summary>8s 退出看门狗（无令牌，退出即终态）：主循环届时仍未返回则强制终结。Exit 不展开栈，
        /// 已显式完成的 Cancel/Stop/Release/unlink 不会被二次执行，无双重释放面；
        /// Exit 前再补一次 Stop（幂等）——封 supervisor 恢复分支 spawn-after-cancel 竞态下
        /// 刚被拉起的子进程（StartCoreAsync 的取消检查点之外的残余窗口）。</summary>
        void StartQuitWatchdog()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                Services.HostLog.Write("[tray] 退出看门狗触发：主循环未返回，强制结束");
                host.Stop();
                Environment.Exit(0);
            });
        }

        // 自更新兜底退出收割器接线（ADR self-update-exit-reaps-dsh-child）：经回收三件套单点
        // （ExitOrchestration.ReapRuntime），不带关窗与看门狗——
        // 自更新路径由 pkexec 脚本接管进程接力，关窗已由 install 委托直退，此处只保证 dsh 不泄漏。
        // 与 orderlyQuit 同款「先回收再退」；StartExitFallback 触发时 supervisorCts/host/marker 均已就绪。
        updateExitReaper = () =>
        {
            Services.HostLog.Write("[update] 兜底回收：cancel 监督器 + 整树击杀 dsh + 释放 marker");
            Services.ExitOrchestration.ReapRuntime(
                () => supervisorCts.Cancel(),
                host.Stop,
                () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), marker.Token));
        };

        // 页面健康观测阶段 1（ADR page-health-monitor）：宿主只读探针轮询，不注入不依赖
        // companion——「dsh 在跑但页面空白」类事故（历史三起全靠人肉发现）从此有自动留痕。
        // 只记录迁移 + 快照进诊断包，绝不自动恢复：误报引发的重启循环比白屏更伤可用性，
        // 是否接线裁决由阶段数据决定。首拍延迟 10s 避开启动空窗，探针异常按 Unknown 续跑。
        healthMonitor = new Services.PageHealthMonitor(windowAccessor, Services.HostLog.Write);
        _ = healthMonitor.RunAsync(TimeSpan.FromSeconds(10), supervisorCts.Token);

        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        if (updateMachine is not null)
        {
            // 就绪横幅（批次三）：ready 到达一次性提示（订阅在窗口句柄就绪后建立，去重防重试期反复弹）
            updateMachine.Subscribe(state =>
            {
                if (state.Status == Services.Update.UpdateStatus.Ready &&
                    state.Version is not null && !readyNotified)
                {
                    readyNotified = true;
                    _ = ShowBannerWhenReady(
                        windowAccessor,
                        Services.Update.UpdateBanner.ReadyScript(state.Version, uiLocale),
                        supervisorCts.Token);
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await updateMachine.StartAsync(supervisorCts.Token);
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

        // 共享 home 切换的启动期告知（ADR shared-home-desktop-profile）：版本底线检查 + 旧 home 一次性提示。
        // 随包插件现于 spawn dsh 前安装（不再「启动后装 → 覆写页面并重启运行时」），横幅无需等安装收尾，
        // 只需等首启引导落定——版本探针用的 bundledClosure 在引导完成时被赋值，提前跑会探到空。
        _ = Task.Run(async () =>
        {
            try
            {
                if (bootstrapSettled is not null)
                {
                    await bootstrapSettled.Task.WaitAsync(TimeSpan.FromSeconds(120), supervisorCts.Token);
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

            var home = HarnessRuntimeHost.ResolveDshHome();
            var detected = await Services.RuntimeVersionGate.ProbeAsync(bundledClosure, supervisorCts.Token);
            if (detected is not null)
            {
                Services.HostLog.Write($"[host] dsh 版本 {detected}（底线 {Services.RuntimeVersionGate.MinimumVersion}）");
                if (Services.RuntimeVersionGate.IsBelowFloor(detected))
                {
                    Services.HostLog.Write($"[host] 警告：dsh {detected} 低于支持底线 {Services.RuntimeVersionGate.MinimumVersion}，已提示用户");
                    await ShowBannerWhenReady(windowAccessor, Services.RuntimeVersionGate.BelowFloorBannerScript(detected, uiLocale), supervisorCts.Token);
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
            if (previousRunUnclean)
            {
                await ShowBannerWhenReady(windowAccessor, RunMarker.UncleanBannerScript(uiLocale), supervisorCts.Token);
            }
        });

        Services.HostLog.Write("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            app.Run();
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[host] Ryn Run 异常：{ex}");
        }

        Services.HostLog.Write("[host] Ryn Run 结束");
        supervisorCts.Cancel();
        bootstrapCts?.Cancel();
        try
        {
            supervisorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 监督任务随宿主回收而结束；无需上报
        }

        host.Stop();
        RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), marker.Token);
        // 非 orderly 退出路径（用户直接关窗使 Run 返回）也要释放单实例锁地址：
        // orderly 路径已 Dispose 过，幂等守卫保证此处安全
        instanceListener?.Dispose();
        return 0;

        /// <summary>当前平台的更新资产 RID（与 release 资产命名后缀对应）。</summary>
        static string UpdateRid()
        {
            if (OperatingSystem.IsLinux())
            {
                return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            }

            if (OperatingSystem.IsWindows())
            {
                return "win-x64";
            }

            if (OperatingSystem.IsMacOS())
            {
                return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            }

            return "unknown";
        }

        /// <summary>安装授权通过后的兜底退出：窗口 Close 未生效时强制结束进程，放行安装脚本。
        /// 改掉裸 <c>Environment.Exit(0)</c>——GTK 主循环滞留时它会绕过 Main 尾部的 <c>host.Stop()</c>，
        /// dsh 子进程成孤儿占住首选端口（ADR self-update-exit-reaps-dsh-child，v0.3.11 实机复现）。
        /// 兜底强退前先经 <see cref="updateExitReaper"/> 确定性收割（cancel 监督器 + 整树击杀 dsh + 释放 marker），
        /// reaper 未接线时回退直杀 dsh，仍不泄漏。与托盘有序退出编排同款三件套，<c>host.Stop()</c> 幂等无双重收割面。</summary>
        void StartExitFallback(CancellationToken ct)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Services.HostLog.Write("[update] 退出兜底触发：回收 dsh 后强退");
                if (updateExitReaper is not null)
                {
                    updateExitReaper();
                }
                else
                {
                    host.Stop();
                }

                Environment.Exit(0);
            });
        }

        /// <summary>窗口就绪后注入横幅：Current 未就绪的 InvalidOperationException 逐秒重试（上限 30 次）；
        /// 其余异常记日志放弃——横幅是增强告知，绝不拖垮启动链路。</summary>
        static async Task ShowBannerWhenReady(CurrentWindowAccessor accessor, string script, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 30 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await accessor.Current.EvaluateJavaScriptAsync(script);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // 窗口尚未创建/已销毁：稍后重试。一次性提示必须送达，与 PushUpdateState 的丢弃策略不同
                }
                catch (Exception ex)
                {
                    Services.HostLog.Write($"[host] 横幅注入失败：{ex.Message}");
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>hide-to-tray：先采样窗口态留证，再把窗口藏起来而非销毁。失败只留日志，不拖垮关窗链路。</summary>
        async Task HideForTrayAsync(IRynWindow window)
        {
            try
            {
                // 原生查询 IRynWindow.IsMaximized（Ryn 0.30.3 起暴露，本仓自 0.30.4 消费）
                Volatile.Write(ref maximizedAtHide, window.IsMaximized ? 1 : 0);
                // 隐藏即采样的留痕+主线程活性证据：唤回行为异常的排查需要知道「隐藏时看到什么」
                Services.HostLog.Write($"[tray] 窗口隐藏到托盘（隐藏前最大化采样={Volatile.Read(ref maximizedAtHide)}）");
            }
            catch (Exception ex)
            {
                // deferred 代理在窗口未就绪时可能抛出：按未知处理，唤回路径对未知不动作
                Volatile.Write(ref maximizedAtHide, -1);
                Services.HostLog.Write($"[tray] 最大化采样失败：{ex.Message}");
            }

            try
            {
                await window.HideAsync();
                Services.HostLog.Write("[tray] 窗口已隐藏");
            }
            catch (Exception ex)
            {
                Services.HostLog.Write($"[tray] 隐藏窗口失败：{ex.Message}");
            }
        }

        /// <summary>路径等值判定（Windows 不区分大小写）——旧 home 提示的指回守卫用。</summary>
        static bool PathsEqual(string a, string b) =>
            string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// <summary>
        /// 推送一条引导进度到引导页（fire-and-forget）。首次推送与页面加载存在竞态
        /// （EvaluateJavaScriptAsync 在页面未就绪时抛 InvalidOperationException），带有限重试——
        /// 引导页是引导期唯一 UI，丢弃会让用户对着无反馈的页面。帧 JSON 经 AppJson 源生成通道，
        /// 与 PushUpdateState 同款 CustomEvent 注入形态。
        /// </summary>
        static async Task PushBootstrapStateAsync(CurrentWindowAccessor accessor, string step, string message, bool failed)
        {
            // detail 必须是帧对象本身的 JSON（页面直接读 detail.step 等，无 JSON.parse）——
            // 与 PushUpdateState 的 state.ToJson() 同款形态，禁止二次包字符串
            var frameJson = JsonSerializer.Serialize(
                new BootstrapStateFrame(step, message, failed),
                Services.AppJsonContext.Default.BootstrapStateFrame);
            var script = "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-bootstrap',{detail:"
                + frameJson
                + "}));}catch(e){}})();";
            for (var attempt = 0; attempt < 15; attempt++)
            {
                try
                {
                    await accessor.Current.EvaluateJavaScriptAsync(script);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // 页面/窗口未就绪：稍后重试
                }
                catch (Exception ex)
                {
                    Services.HostLog.Write($"[bootstrap] 进度推送失败（放弃）：{ex.Message}");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400));
            }

            Services.HostLog.Write("[bootstrap] 进度推送重试耗尽（页面始终未就绪）");
        }

        /// <summary>
        /// 引导重试循环：单次尝试（RuntimeBootstrap.RunAsync）→ 成功返回运行时；失败推错误态并等待
        /// 重试信号（desktop.bootstrap.retry 经 <paramref name="gate"/> 放行）或应用退出
        /// （<paramref name="ct"/> 取消）。返回 null = 放弃（退出/取消）。
        /// </summary>
        async Task<(string NodeExe, string DshEntry)?> RunBootstrapWithRetryAsync(
            Services.RuntimeBootstrapGate gate, CancellationToken ct)
        {
            var options = Services.RuntimeBootstrapOptions.Load(AppContext.BaseDirectory);
            var runtimeDir = RuntimeLocator.ResolveDownloadedRuntimeDirectory();
            var hooks = Services.RuntimeBootstrap.CreateDefaultHooks(Services.HostLog.Write);
            Services.HostLog.Write($"[bootstrap] 引导开始：runtimeDir={runtimeDir} dshSpec={options.DshSpec} node=v{options.NodeVersion}");

            while (true)
            {
                gate.Reset();
                BootstrapOutcome outcome;
                try
                {
                    outcome = await Services.RuntimeBootstrap.RunAsync(
                        options,
                        runtimeDir,
                        progress => _ = PushBootstrapStateAsync(windowAccessor, progress.Step.ToString(), progress.Message, progress.Failed),
                        hooks,
                        ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (outcome.Success && outcome.Runtime is { } runtime)
                {
                    return runtime;
                }

                var reason = outcome.Error ?? "未知错误";
                Services.HostLog.Write($"[bootstrap] 引导失败：{reason}（等待用户重试或退出）");
                // 推实际失败步骤：进度页据此红色高亮失败环节（推 "Ready" 会让高亮不可达）
                await PushBootstrapStateAsync(windowAccessor, outcome.Step.ToString(), reason, failed: true);

                // 等重试信号或应用退出；信号与取消都是即时语义事件，200ms 轮询足够
                while (!gate.IsSignaled && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                }

                if (ct.IsCancellationRequested)
                {
                    return null;
                }
            }
        }

        /// <summary>把状态变化推给页面：插件监听 <c>dsh-desktop-update</c> CustomEvent 渲染更新按钮。</summary>
        static void PushUpdateState(CurrentWindowAccessor? accessor, Services.Update.UpdateState state)
        {
            try
            {
                // Current 在窗口未创建/已关闭时抛异常（非返回 null）：启动早期与退出阶段都会走到
                if (accessor?.Current is null)
                {
                    return;
                }

                _ = accessor.Current.EvaluateJavaScriptAsync(
                    "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-update',{detail:"
                    + state.ToJson()
                    + "}));}catch(e){}})();");
            }
            catch (InvalidOperationException)
            {
                // 窗口尚未就绪：本次推送丢弃，后续状态变化会再推
            }
        }

        /// <summary>
        /// 运行一次 <c>dsh plugin add</c> 子进程（写入桌面 profile），供 <see cref="Services.MarketInstallHelper.EnsureMarketFromRegistryAsync"/>
        /// 注入执行。env（DSH_HOME + pnpm store/cache 重定向 + UTF-8 流）已由该 helper 配好；
        /// 这里只负责 spawn、双流并发读、取消传递。
        /// </summary>
        async Task<(int Exit, string Out, string Err)> RunDshPluginAddAsync(
            System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                throw new InvalidOperationException("无法启动 dsh plugin 进程");
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
    }
}
