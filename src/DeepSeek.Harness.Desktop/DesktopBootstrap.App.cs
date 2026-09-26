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
        // 导航靶点初值：壳 origin（dsh 就位即直载壳 URL；dsh 未起为 null，健康 reload 跳过）。
        // token 只活在铸币链（StartRuntime/收养/落定重铸），永不进导航靶点。
        _webUrl = runtime.WebUrl is not null ? DshShellForward.ShellRoot : null;

        // 托盘与窗口共用同一 icon 资产；缺失时托盘不注册（关窗保持直退，见 IsReady）
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        bool trayAvailable = File.Exists(iconPath); // verify-code-conventions: ignore 组合根装配：icon 存在性探测是配置面，非业务/领域直调
        _tray.ConfigureIcon(iconPath, trayAvailable);

        _app = RynApplication.CreateBuilder()
            // 壳 scheme 注册（initial navigation 之前；Ryn 保留 `ryn`，此处用自有 `dsh-app`）
            .ConfigureCustomScheme(DshShellForward.ShellScheme, PageBridge.DshSchemeBridge.Handler(_shellForward))
            .ConfigureOptions(opts =>
            {
                // IPC 桥接白名单显式登记壳 origin（Ryn 默认只认 ryn://app；自举显式更稳，不依赖隐式追加）。
                opts.AllowedOrigins.Add(DshShellForward.ShellOrigin);
                if (runtime.WebUrl is not null)
                {
                    // 壳 origin 直载（对齐上游 dsh-app://app）：窗口永远只进壳 URL，
                    // token/cookie 永不进页面（铸币在各 epoch 起点落定，见 EnterMainUiAsync/收养）。
                    opts.Url = DshShellForward.ShellRoot;
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
            .ConfigureServices(services => RegisterServices(services, preflight, update))
            .Build();

        _windowAccessor = _app.Services.GetRequiredService<CurrentWindowAccessor>();

        return new AppSetup(_app, _windowAccessor);
    }

    private void RegisterServices(IServiceCollection services, Preflight preflight, UpdateSetup update)
    {
        services.AddRynCommands();
        // 宿主导航回调（Ryn 0.32.0 Ryn.Callbacks）：在导航边界统一拦截外部链接（ADR ryn-navigation-callbacks）。
        // 当前页面 origin 即壳 origin（页面永驻壳内；dsh 绝对链接由转发层改写回壳，外链照走系统浏览器）。
        services.AddRynCallbacks();
        services.AddRynNavigationCallbacks();
        // 覆盖源生成的 handler 无参注册：导航回调依赖（openExternal 打开器 / 日志 /
        // 当前页面 origin）在 ConfigureServices 时已知，经工厂注入。
        services.AddSingleton(sp => new RynNavigationCallbacks(
            opener: null,
            log: HostLog.Write,
            currentOrigin: DshShellForward.ShellOrigin,
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

    /// <summary>收养后导航：epoch 可能已换（新 secret/端口）→ 先重铸（覆盖式，每次全量 HTTP），再定导航。
    /// 页内已自刷即免导航，只做收养登记（_webUrl 恒为壳根）。</summary>
    private ValueTask NavigateAfterAdoptAsync(AppSetup app, RynNavigationCallbacks navCallbacks, Uri url)
    {
        // webUrl 恒壳根（导航靶点与健康 reload 靶点）；收养登记只刷新它，dsh 旧 URL 不再进导航。
        // 页面永驻壳内：无需逐跳授权（Ryn 非 http origin 拒绝运行时授权，桥接白名单已在 BuildApp 装配）；
        // dsh 自指 3xx 由转发层内部跟完，页内绝对 dsh 链接走导航回调外部策略（fail-closed），外链照走系统浏览器。
        _webUrl = DshShellForward.ShellRoot;
        // 同步编排沿用既有形态：重铸内部超时兜底，无 ct 位（收养回调无取消语义）；失败 loud，导航照发
        // （转发 401/502 → 探针/恢复面按错误页处理）。
        _ = _shellForward.MintAsync(DshWebUrl.From(url), HostLog.Write, CancellationToken.None).GetAwaiter().GetResult();
        // 免导航（ADR adopt-skip-navigate-on-self-reload）：周期内有到达即视为页内自刷
        // （市场 doRestart 轮询到新 boot 即 location.reload；谓词只比时间戳，同源靠前提假设），
        // 再导航即多余——只做收养登记，跳过实际导航。无到达时走壳单跳。
        if (AdoptNavigateGate.ShouldSkipAdoptNavigate(navCallbacks.LastNavigatedAtUtc, _lastRecoveryShownAtUtc))
        {
            HostLog.Write($"[nav] 收养时恢复周期内已有页面到达（视为页内自刷，{url.GetLeftPart(UriPartial.Authority)}），跳过壳侧导航");
            return ValueTask.CompletedTask;
        }

        return app.WindowAccessor.Current.NavigateAsync(DshShellForward.ShellRoot);
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

    /// <summary>引导完成后的壳侧导航收尾：窗口进壳 origin 单跳直达；导航前必先铸币（本方法是 bootstrap 路径的
    /// epoch 起点，覆盖式重铸）。由引导服务在 dsh 就位时回调。</summary>
    /// <param name="app">Ryn 应用装配产出（窗口访问器与回调服务来源）。</param>
    /// <param name="url">dsh 就位端点（仅供落定重铸与日志；导航一律走壳 URL）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task EnterMainUiAsync(AppSetup app, DshWebUrl url, CancellationToken ct)
    {
        _webUrl = DshShellForward.ShellRoot;
        // 窗口可能尚未建好（原生建窗慢于 dsh 就位时，首个 Current 即抛，ADR bootstrap-window-ready-wait）：
        // 有界等可用，超时 loud 跳过本次导航（dsh 已就绪，重启即进）。
        if (!await WaitForWindowAsync(app, ct).ConfigureAwait(false))
        {
            HostLog.Write($"[nav] 等窗口可用超时（{_timeouts.WindowReadyTimeoutSeconds}s），跳过本次进入主界面导航");
            return;
        }
        // 壳铸币（bootstrap 路径 dsh 在 StartRuntime 之后才就位，此处是 epoch 起点；覆盖式重铸，
        // MintAsync 无 epoch 跟踪，每次全量 HTTP。无 mint 即导航 → 转发 502 白页
        // （dispatch 36273205175 arm64 实证），故导航前必铸）。
        _ = await _shellForward.MintAsync(url, HostLog.Write, ct).ConfigureAwait(false);
        // 壳单跳直达：同站内无 token、无 cookie 链（IPC 桥接白名单已在 BuildApp 经 AllowedOrigins 装配；
        // Ryn 非 http origin 拒绝运行时授权，此处不再逐跳授权）。
        // 第二跳等提交的旧语义退役（单跳无合并问题）；提交等待仍有界（NavCommitTimeoutSeconds）。
        await NavigateAndAwaitCommitAsync(app, DshShellForward.ShellRoot, ct);
        await SettleWebSessionAsync(app, url, ct);
    }
}
