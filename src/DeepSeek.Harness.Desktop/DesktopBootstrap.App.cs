using Microsoft.Extensions.DependencyInjection;
using Ryn.Callbacks;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的应用装配与后台接线面（唯一 dot 分部，ADR composition-root-value-flow-pipeline
/// 批次 3 分部终态）：Ryn 应用构建、命令路由/托盘服务注册、监督器与退出接线、健康监视/横幅后台任务、
/// WebView 导航原语。启动主链与阶段编排在 <c>DesktopBootstrap.cs</c>。
/// </summary>
public sealed partial class DesktopBootstrap
{
    private AppSetup BuildApp(Preflight preflight, RuntimeSetup runtime, UpdateSetup update)
    {
        // 运行时就位 URL 的消费点（值流）：StartRuntime 产出、此处落位为壳侧导航靶点初值；
        // 引导完成/崩溃恢复导航会再刷新该字段（见下方导航原语与监督器 navigate）。
        _webUrl = runtime.WebUrl?.Value;

        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 IsReady）
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        bool trayAvailable = File.Exists(iconPath); // verify-code-conventions: ignore 组合根装配：icon 存在性探测是配置面，非业务/领域直调
        _tray.ConfigureIcon(iconPath, trayAvailable);

        _app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                if (runtime.WebUrl is { } webUrl)
                {
                    // dsh web UI（loopback；完整运行时随应用内置后仍是此路径）
                    opts.Url = webUrl.Value;
                }
                else
                {
                    // 降级：dsh 未起时展示本地占位页，保证壳仍可开
                    opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                }

                opts.Title = "DeepSeek Harness Desktop";
                opts.Width = 1200;
                opts.Height = 800;
                // A 类启动配置经类型化值消费（批次 3）：dev 后缀规则封装进 LaunchOptions。
                opts.ApplicationId = preflight.Launch.ApplicationIdFor("io.github.ZK-Andy.dotnet-deepseek-harness-desktop");
                if (File.Exists(iconPath)) // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                {
                    opts.IconPath = iconPath;
                }
                else
                {
                    HostLog.Write($"[host] icon 缺失：{iconPath}");
                }

                HostLog.Write($"[host] Ryn opts: Url={(runtime.WebUrl is { } logged ? logged.ToString() : "null")} ApplicationId={opts.ApplicationId} Icon={(File.Exists(iconPath) ? iconPath : "missing")}"); // verify-code-conventions: ignore 组合根装配：icon 探测是配置面
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(services => RegisterServices(services, preflight, runtime, update))
            .Build();

        _windowAccessor = _app.Services.GetRequiredService<CurrentWindowAccessor>();

        return new AppSetup(_app, _windowAccessor);
    }

    private void RegisterServices(IServiceCollection services, Preflight preflight, RuntimeSetup runtime, UpdateSetup update)
    {
        services.AddRynCommands();
        // 宿主导航回调（Ryn 0.32.0 Ryn.Callbacks）：在导航边界统一拦截外部链接（ADR ryn-navigation-callbacks）。
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        // 覆盖源生成的 handler 无参注册：导航回调依赖（openExternal 打开器 / 日志 /
        // 当前页面 origin）在 ConfigureServices 时已知，经工厂注入。
        services.AddSingleton(sp => new RynNavigationCallbacks(
            opener: null,
            log: HostLog.Write,
            currentOrigin: runtime.WebUrl?.Authority,
            // 外部链接打开失败 → 推事件给页面，companion 渲染 toast（R2 N2）。EmitEvent 走
            // deferred IRynWebView（窗口就绪后转发），在导航回调触发时页面必然已加载。
            notifyLinkFail: url => sp.GetRequiredService<IRynWebView>().EmitEvent(
                "desktop.externalLinkOpenerFailed",
                new ExternalLinkOpenerFailedFrame(url),
                AppJsonContext.Default.ExternalLinkOpenerFailedFrame)));
        // 外部链接 → 系统默认浏览器（宿主命令路由，见 implemented ADR open-external-links-in-system-browser）
        services.AddSingleton<ICommandRouter>(new ExternalLinkCommandRouter(log: HostLog.Write));
        // dsh 语言变更桥接（desktop.companion.setLocale，ADR host-ui-locale）
        services.AddSingleton<ICommandRouter>(new CompanionLocaleCommandRouter(_uiLocale, log: HostLog.Write));
        // 引导页语言拉取（desktop.ui.getLocale）：页面在 dsh 启动前渲染，语言只能由宿主给
        services.AddSingleton<ICommandRouter>(new UiLocaleCommandRouter(_uiLocale));
        // 诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）
        services.AddSingleton<ICommandRouter>(new DesktopDiagnosticsCommandRouter(
            log: HostLog.Write, healthSnapshot: () => _healthMonitor?.Snapshot));
        // 恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
        // 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）
        services.AddSingleton<ICommandRouter>(sp => new RecoveryCommandRouter(
            closeWindow: () => sp.GetRequiredService<IRynWindow>().Close(),
            _tray.CloseGate,
            HostLog.Write));
        // 引导重试命令（desktop.bootstrap.retry，ADR online-first-unbundled-runtime）：
        // wwwroot 引导页的重试按钮 → 闸门放行引导循环。gate 实例在 Run 顶部创建，
        // 引导任务与路由共用同一实例
        services.AddSingleton<ICommandRouter>(new BootstrapCommandRouter(
            preflight.Bootstrap.Gate, HostLog.Write));
        // 插件引导决策命令（desktop.preinstall.choose，ADR reference-alignment 批次二）：
        // wwwroot 引导页「插件引导」步的确认装/跳过 → 闸门放行引导任务
        services.AddSingleton<ICommandRouter>(new PreinstallCommandRouter(
            preflight.Bootstrap.PreinstallGate, HostLog.Write));
        // 开机自启开关（desktop.autostart.getState/set）
        services.AddSingleton<ICommandRouter>(new AutostartCommandRouter(log: HostLog.Write));
        // 关闭最小化到托盘偏好（desktop.closeToTray.getState/set）；available 惰性求值——
        // 服务注册早于托盘初始化，trayReady 由外层闭包稍后赋值
        services.AddSingleton<ICommandRouter>(new Tray.CloseToTrayCommandRouter(
            _tray.CloseBehavior, () => _tray.IsReady, log: HostLog.Write));
        // 自更新命令：desktop.update.getState / check / install（dev 门禁下不注册路由，invoke 自然失败）
        if (update.Updates.Machine is { } updateMachine)
        {
            services.AddSingleton<ICommandRouter>(new Update.DesktopUpdateCommandRouter(updateMachine, log: HostLog.Write, backgroundToken: () => _supervisorCtsRef?.Token ?? CancellationToken.None));
        }
        RegisterTrayServices(services, update);
    }

    /// <summary>托盘服务注册（ADR shell-tray-hide-to-tray）：图标+菜单 + 事件路由（窗口动作经委托接 deferred 代理）。</summary>
    private void RegisterTrayServices(IServiceCollection services, UpdateSetup update)
    {
        // 托盘（批次三）：图标+菜单；点击语义经 companion 中继
        // 回 desktop.tray.event 在宿主解析——EmitEvent 是 Ryn 插件内部属性，不在源生成通道
        if (_tray.IsAvailable)
        {
            services.AddRynTray(o =>
            {
                o.IconPath = _tray.IconPath;
                o.Tooltip = "DeepSeek Harness Desktop";
            });
        }
        // 托盘事件路由：窗口动作经委托接 deferred 代理（注册期无需窗口就绪；
        // 委托注入让退出顺序契约可用记序 fake 测试）
        services.AddSingleton<ICommandRouter>(sp => new Tray.DesktopTrayCommandRouter(
            showWindow: () => _tray.RecallAsync(),
            closeWindow: () =>
            {
                // 管道在 supervisorCts 声明后接线，而托盘退出必经托盘菜单的用户交互、
                // 必然晚于接线，故此处不可能为 null
                _exit.OrderlyQuit();
            },
            _tray.CloseGate,
            update.Updates.Machine,
            _uiLocale,
            HostLog.Write,
            notify: (title, message) =>
                sp.GetRequiredService<TrayService>().ShowNotification(title, message)));
    }

    /// <summary>托盘就绪化（装配壳）：解析 Ryn 托盘服务并交给控制器（无托盘环境传 null）。</summary>
    private void ShowTray(AppSetup app)
    {
        _tray.Show(_tray.IsAvailable
            ? () => app.App.Services.GetRequiredService<TrayService>()
            : null);
    }

    private SupervisorSetup SetupSupervisor(Preflight preflight, AppSetup app, HostSetup host)
    {
        // 原 `using var supervisorCts`：生命周期由 Run 的 finally 释放（本方法接线 _supervisorCtsRef）。
        var cts = new CancellationTokenSource();
        _supervisorCtsRef = cts; // 自更新后台任务 token 持有器接线（见顶部声明）
        RynNavigationCallbacks navCallbacks = app.App.Services.GetRequiredService<RynNavigationCallbacks>();
        var supervisor = new RuntimeSupervisor(
            host.Host,
            restartTimeout: TimeSpan.FromSeconds(_timeouts.SupervisorRestartTimeoutSeconds),
            recoveredRetryDelay: TimeSpan.FromSeconds(_timeouts.SupervisorRecoveredRetryDelaySeconds),
            failedRetryDelay: TimeSpan.FromSeconds(_timeouts.SupervisorFailedRetryDelaySeconds),
            showRecovery: isLockBlocked => ShowRecoveryPageAsync(app, host, isLockBlocked),
            navigate: url => NavigateAfterAdoptAsync(app, navCallbacks, url),
            log: HostLog.Write);
        // 引导期门控：宿主尚无 dsh 进程时 WaitForExitAsync 立即完成，监督器会空转进恢复循环
        // 并用恢复屏覆写引导页——必须等引导落定（成功 spawn 或确认放弃）才进入监视。
        var supervisorTask = Task.Run(async () =>
        {
            // 引导落定握手（理由见上方「引导期门控」注释；网 = BootstrapSettleGateTests）。
            if (!await preflight.Bootstrap.WaitSettledAsync(timeout: null, cts.Token))
            {
                return;
            }

            await supervisor.RunAsync(cts.Token);
        });

        // 有序退出编排 + 自更新兜底收割器接线（WireExitHandlers）
        WireExitHandlers(app.App.Services.GetRequiredService<IRynWindow>(), host, cts);

        return new SupervisorSetup(cts, supervisorTask);
    }

    /// <summary>单实例退出管道接线（ADR composition-root-value-flow-pipeline）：有序退出步骤即构造数据，
    /// 托盘有序退出与 Run 尾部共用同一实例，幂等由 once-guard 保证；运行时回收先于关窗。</summary>
    private void WireExitHandlers(IRynWindow quitWindow, HostSetup host, CancellationTokenSource supervisorCts)
    {
        _exit = new Core.ExitPipeline(
            supervisorCts.Cancel,
            host.Host.Stop,
            () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), host.Marker.Token),
            () => _instanceListener?.Dispose(),
            quitWindow.Close,
            log: HostLog.Write);
    }

    /// <summary>恢复屏展示 + 恢复周期起点打点（ADR adopt-skip-navigate-on-self-reload）。</summary>
    /// <param name="isLockBlocked">残留锁死跳过重启（ADR residue-lock-fail-loud）：原因取锁文案（恢复页提示），否则普通崩溃原因。</param>
    private ValueTask ShowRecoveryPageAsync(AppSetup app, HostSetup host, bool isLockBlocked = false)
    {
        // 周期起点：子进程退出后、RestartAsync 等待前。周期内的导航到达即页内自刷
        // （市场 doRestart 轮询到新 boot 即 reload），收养 navigate 据此免导航。
        _lastRecoveryShownAtUtc = DateTimeOffset.UtcNow;
        // 恢复页三件套（ADR diag-masking-and-recovery-page）：失败原因 + stderr 尾部展示 +
        // 导出诊断/退出动作。desktop.* 走 Ryn 层 IPC 不依赖 dsh 存活；数据经 textContent
        // 回填（stderr 是上游不可控输出，绝不 innerHTML 拼接）
        var tail = host.Host.StderrTail.TakeLast(12).ToList();
        string reason = isLockBlocked
            ? UiCopy.ReasonDshResidueLocked(_uiLocale.IsEnglish)
            : UiCopy.ReasonRuntimeCrashed(_uiLocale.IsEnglish);
        _ = app.WindowAccessor.Current.EvaluateJavaScriptAsync(
            RecoveryPageBuilder.BuildScript(reason, tail, _uiLocale.IsEnglish));
        return ValueTask.CompletedTask;
    }

    /// <summary>收养后导航：页内已自刷即免导航，只做收养登记。</summary>
    private ValueTask NavigateAfterAdoptAsync(AppSetup app, RynNavigationCallbacks navCallbacks, Uri url)
    {
        // webUrl 同步刷新——健康监视器（有界恢复）靠它作为 reload 靶点；若崩溃重启用新端口
        // （ADR child-process-reaping-port-drift 的端口漂移）而 webUrl 仍指向旧 URL，
        // 监视器的 reload 会打到已死的旧端口、甚至覆写刚恢复的导航。
        _webUrl = url;
        AuthorizeIpcOriginFor(app.WindowAccessor, url);
        // 免导航（ADR adopt-skip-navigate-on-self-reload）：周期内有到达即视为页内自刷
        // （市场 doRestart 轮询到新 boot 即 location.reload；谓词只比时间戳，同源靠前提假设），
        // 再 NavigateAsync 即第二跳——只做收养登记（上文 _webUrl + origin 授权），跳过实际导航。
        // 无到达时走既有导航。
        if (AdoptNavigateGate.ShouldSkipAdoptNavigate(navCallbacks.LastNavigatedAtUtc, _lastRecoveryShownAtUtc))
        {
            HostLog.Write($"[nav] 收养时恢复周期内已有页面到达（视为页内自刷，{url.GetLeftPart(UriPartial.Authority)}），跳过壳侧导航");
            return ValueTask.CompletedTask;
        }

        return app.WindowAccessor.Current.NavigateAsync(url);
    }

    private void SetupHealthMonitor(AppSetup app, SupervisorSetup supervisor)
    {
        // 页面健康观测 + 有界恢复（ADR page-health-monitor / reference-alignment 批次五）：
        // 宿主只读探针轮询，不注入不依赖 companion——「dsh 在跑但页面空白」类事故（历史三起全靠
        // 人肉发现）从此有自动留痕；连续 Dead 达阈值后在预算内触发一次有界 reload，耗尽转观测-only，
        // 成功恢复复位预算（防误报引发无限重载循环，对齐参照 plugin_boot.rs 的有界刷新门控）。
        // 首拍延迟（HealthInitialDelaySeconds）避开启动空窗，探针异常按 Unknown 续跑。reload 委托捕获 webUrl（字段，
        // 初始/引导完成/崩溃恢复导航三处都会刷新，见上文与 RuntimeSupervisor 的 navigate），
        // 恒为当前 dsh web 靶点；webUrl 只有当引导未落定（dsh 未起）才为空，而该窗口页面是
        // wwwroot 引导页（有内容 → Alive），不会进入 Dead 恢复分支——reload 委托的空态只是防御性兜底。
        _healthMonitor = new PageHealthMonitor(
            app.WindowAccessor,
            HostLog.Write,
            reload: ct => _webUrl is null
                ? ValueTask.CompletedTask
                : app.WindowAccessor.Current.NavigateAsync(_webUrl, ct));
        _ = _healthMonitor.RunAsync(TimeSpan.FromSeconds(_timeouts.HealthInitialDelaySeconds), supervisor.Cts.Token);
    }

    private void SharedHomeBannerTask(Preflight preflight, AppSetup app, HostSetup host, SupervisorSetup supervisor)
    {
        // 共享 home 切换的启动期告知（ADR shared-home-desktop-profile）：版本底线检查 + 旧 home 一次性提示。
        // 随包插件现于 spawn dsh 前安装（不再「启动后装 → 覆写页面并重启运行时」），横幅无需等安装收尾，
        // 只需等首启引导落定——版本探针走 PATH 上全局 dsh（bundled=null），提前跑会探到空。
        _ = Task.Run(async () =>
        {
            // 引导落定前横幅不抢跑；超时（BootstrapSettleTimeoutSeconds）按已定继续（降级语义在 BootstrapSettleGate 内），取消即放弃。
            if (!await preflight.Bootstrap.WaitSettledAsync(TimeSpan.FromSeconds(_timeouts.BootstrapSettleTimeoutSeconds), supervisor.Cts.Token))
            {
                return;
            }

            string home = HarnessRuntimeHost.ResolveDshHome();
            string? detected = await RuntimeVersionGate.ProbeAsync(supervisor.Cts.Token);
            if (detected is not null)
            {
                HostLog.Write($"[host] dsh 版本 {detected}（底线 {RuntimeVersionGate.MinimumVersion}）");
                if (RuntimeVersionGate.IsBelowFloor(detected))
                {
                    HostLog.Write($"[host] 警告：dsh {detected} 低于支持底线 {RuntimeVersionGate.MinimumVersion}，已提示用户");
                    await PagePump.ShowBannerWhenReadyAsync(app.WindowAccessor, DesktopBanner.BuildVersionFloorBanner(detected, _uiLocale), supervisor.Cts.Token);
                }
            }
            else
            {
                HostLog.Write("[host] dsh 版本探测失败，跳过底线检查");
            }

            // 旧 home 留痕仅进日志（界面横幅已按用户拍板去除，ADR companion-settings-consolidation）；
            // 指回旧目录时不记「改用新目录」——自相矛盾且无信息量
            if (LegacyHomeNotice.IsPresent() && !PathsEqual(home, LegacyHomeNotice.LegacyPrivateHome))
            {
                HostLog.Write($"[host] 检测到旧版桌面数据目录 {LegacyHomeNotice.LegacyPrivateHome}；新版使用 {home}（未迁移）");
            }

            // 上轮非受控退出：提示但不暗示应用故障（用户杀进程也属此类），引导导出诊断
            if (host.Marker.PreviousRunUnclean)
            {
                await PagePump.ShowBannerWhenReadyAsync(app.WindowAccessor, DesktopBanner.BuildUncleanExitBanner(_uiLocale), supervisor.Cts.Token);
            }
        });
    }

    /// <summary>引导完成后的壳侧导航收尾（ADR bootstrap-cross-scheme-cookie-401）：记录 webUrl 后
    /// WebKitGTK 两跳导航进主界面，由引导服务在 dsh 就位时回调。</summary>
    /// <param name="app">Ryn 应用装配产出（窗口访问器与回调服务来源）。</param>
    /// <param name="url">dsh 就位端点。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task EnterMainUiAsync(AppSetup app, DshWebUrl url, CancellationToken ct)
    {
        _webUrl = url.Value;
        // WebKitGTK 两跳导航：从自定义 scheme 占位页（ryn://app）发起的跨 scheme 导航链上，
        // dsh 的 SameSite=Strict 会话 cookie 不随 303 回环重定向发送（沙箱实锤 2026-09-14：
        // mint 命中 → 随后 GET / 无 cookie 401）。先落裸 origin http 页脱离 ryn:// 链路
        // （该跳无 token 必得 401，瞬时无害），再从 http 页发起同站导航——Strict cookie 正常随行。
        // 第二跳必须等第一跳真正提交（NavigateAsync 连发会被 WebKitGTK 合并成一次导航）。
        Uri landing = url.AuthorityRoot;
        AuthorizeIpcOriginFor(app.WindowAccessor, landing);
        await NavigateAndAwaitCommitAsync(app, landing, ct);
        // 第二跳同样等提交（R2 S2）：否则探针采到旧落地误触发重进；提交等待有界（NavCommitTimeoutSeconds）。
        await NavigateAndAwaitCommitAsync(app, url.Value, ct);
        await SettleWebSessionAsync(app, url, ct);
    }

    /// <summary>导航前授权 <paramref name="url"/> 的 origin 可 IPC（Ryn 0.38 受信 origin 集合，ADR
    /// ryn-pr91-trusted-origin）：端口漂移/引导后首次进入 dsh 时页面 origin 不在初始受信集里，
    /// 未授权则页面命令通道被拒（token 有效也拒）。幂等，重复授权无害；授权在导航前生效，
    /// 「bridge 随下一次导航安装」。窗口未就绪时异常照抛（fail loud，调用面均已在窗口就绪后）。</summary>
    /// <param name="accessor">当前窗口访问器。</param>
    /// <param name="url">待授权 URL。</param>
    private void AuthorizeIpcOriginFor(CurrentWindowAccessor accessor, Uri url)
    {
        string origin = url.GetLeftPart(UriPartial.Authority);
        accessor.Current.AuthorizeIpcOrigin(origin);
        HostLog.Write($"[nav] 已授权 IPC origin：{origin}");
    }

    /// <summary>导航并等待其真正提交（<see cref="RynNavigationCallbacks"/> 的
    /// 「导航已到达」信号，先订阅后导航避免错过）。提交信号用于隔开两跳导航——
    /// <c>NavigateAsync</c> 连发会被 WebKitGTK 合并，前一跳尚未发出即被后一跳覆盖。
    /// 等待超时按「已提交」降级继续（信号只是隔跳手段，缺位时不比单跳直导更差）；
    /// 取消（应用退出）照常传播。</summary>
    /// <param name="app">Ryn 应用装配产出（导航回调服务来源）。</param>
    /// <param name="target">导航靶点。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task NavigateAndAwaitCommitAsync(AppSetup app, Uri target, CancellationToken ct)
    {
        RynNavigationCallbacks callbacks =
            app.App.Services.GetRequiredService<RynNavigationCallbacks>();
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        callbacks.SetOnNavigated(() => arrived.TrySetResult());
        try
        {
            HostLog.Write($"[nav] 发起导航：{target.GetLeftPart(UriPartial.Authority)}");
            await app.WindowAccessor.Current.NavigateAsync(target);
            HostLog.Write($"[nav] 导航调用已返回：{target.GetLeftPart(UriPartial.Authority)}");
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(_timeouts.NavCommitTimeoutSeconds), ct);
            }
            catch (TimeoutException)
            {
                HostLog.Write($"[nav] 等待导航提交信号超时（{_timeouts.NavCommitTimeoutSeconds}s），按已提交继续");
            }
        }
        finally
        {
            callbacks.SetOnNavigated(static () => { });
        }
    }
}
