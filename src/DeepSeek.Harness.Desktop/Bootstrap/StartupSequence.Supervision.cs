using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// <see cref="StartupSequence"/> 的监督与退出接线面：监督器装配（引导落定门控）、退出管道、
/// 失败面承载、恢复后铸币重验、页面健康观测与启动期告知接线。启动主链在 <c>StartupSequence.cs</c>。
/// </summary>
internal sealed partial class StartupSequence
{
    private SupervisorSetup SetupSupervisor(HostSetup host)
    {
        // 监督器取消令牌源由组合根拥有（容器单例，寿命 = 本次 Run；释放随组合根作用域）。
        CancellationTokenSource cts = _app.App.Services.GetRequiredService<CancellationTokenSource>();
        var supervisor = new RuntimeSupervisor(
            host.Host,
            restartTimeout: TimeSpan.FromSeconds(_timeouts.SupervisorRestartTimeoutSeconds),
            recoveredRetryDelay: TimeSpan.FromSeconds(_timeouts.SupervisorRecoveredRetryDelaySeconds),
            failedRetryDelay: TimeSpan.FromSeconds(_timeouts.SupervisorFailedRetryDelaySeconds),
            showRecovery: isLockBlocked => ShowFailureSurfaceAsync(host, isLockBlocked),
            onRecovered: RevalidateAfterRecoveryAsync,
            log: HostLog.Write,
            blockedLogEveryRounds: _timeouts.SupervisorBlockedLogEveryRounds);
        // 引导期门控：宿主尚无 dsh 进程时 WaitForExitAsync 立即完成，监督器会空转进恢复循环
        // 并覆写引导页——必须等引导落定（成功 spawn 或确认放弃）才进入监视。
        var supervisorTask = Task.Run(async () =>
        {
            // 引导落定握手（理由见上方「引导期门控」注释；网 = BootstrapSettleGateTests）。
            if (!await _preflight.Bootstrap.WaitSettledAsync(timeout: null, cts.Token))
            {
                return;
            }

            await supervisor.RunAsync(cts.Token);
        });

        return new SupervisorSetup(cts, supervisorTask);
    }

    /// <summary>失败面承载（ADR native-failure-surface）：原生对话框优先（不碰 DOM），无工具环境回落页内恢复面；起手失效铸币态。</summary>
    /// <param name="host">宿主（stderr 尾部与诊断包来源）。</param>
    /// <param name="isLockBlocked">残留锁死跳过重启（ADR residue-lock-fail-loud）：原因取锁文案，否则普通崩溃原因。</param>
    private ValueTask ShowFailureSurfaceAsync(HostSetup host, bool isLockBlocked = false)
    {
        if (_shellForward.InvalidateRoute())
        {
            HostLog.Write("[shell] dsh 进程退出：铸币态失效，holder 门控重新武装（cookie 保留待收养重验）");
        }

        // 原因选择是域决策（UiCopy.RecoveryReason）；尾部截断数是展示面（本地常量，不入配置）。
        string reason = UiCopy.RecoveryReason(isLockBlocked, _uiLocale.IsEnglish);
        if (_failureDialog.IsAvailable)
        {
            // 一周期一次（闩在 onRecovered 解锁）；上一个模态未处置完则不再叠新的（模态是阻塞原语，会占池线程）。
            if (Interlocked.Exchange(ref _failureSurfaceShown, 1) == 0
                && Interlocked.CompareExchange(ref _failureModalInFlight, 1, 0) == 0)
            {
                _ = ShowNativeFailureAsync(host, reason);
            }

            return ValueTask.CompletedTask;
        }

        return ShowPageFailureSurfaceAsync(host, reason);
    }

    /// <summary>页内恢复面（回落承载面）：覆写活动页 DOM，故恢复后必须补一次收口导航（ADR recovery-navigation-gate-retirement Decision 3）。</summary>
    /// <param name="host">宿主（stderr 尾部来源）。</param>
    /// <param name="reason">人读失败原因。</param>
    private ValueTask ShowPageFailureSurfaceAsync(HostSetup host, string reason)
    {
        const int RecoveryTailLines = 12;
        Volatile.Write(ref _pageSurfaceFallbackShown, 1);
        _ = _app.WindowAccessor.Current.EvaluateJavaScriptAsync(
            RecoveryPageBuilder.BuildScript(reason, host.Host.StderrTail.TakeLast(RecoveryTailLines).ToList(), _uiLocale.IsEnglish));
        return ValueTask.CompletedTask;
    }

    /// <summary>原生失败面（池线程，不阻塞监督循环）：失败原因 + stderr 尾部 + 诊断包路径（正文自裁上限）。
    /// 退出意向单问、极性倒置（ADR native-failure-surface）：问「要退出应用吗？」，<b>破坏性动作只挂显式 true</b>
    /// ——插件在通道故障（探不到工具 / 进程起不来 / 非三平台 / osascript 出错）时恒回 false，故障都落回继续恢复。</summary>
    /// <param name="host">宿主（stderr 尾部来源）。</param>
    /// <param name="reason">人读失败原因。</param>
    private async Task ShowNativeFailureAsync(HostSetup host, string reason)
    {
        try
        {
            string message = RecoveryDialogText.Build(
                reason, host.Host.StderrTail, _diagnosticsExport.Export(), _uiLocale.IsEnglish);
            bool quit = await _failureDialog
                .ConfirmAsync(UiCopy.RecoveryDialogTitle(_uiLocale.IsEnglish), message, CancellationToken.None)
                .ConfigureAwait(false);
            if (quit)
            {
                ExitFromFailureSurface();
            }
        }
        catch (Exception ex)
        {
            // 原生通道故障 → 回落页内恢复面：失败面绝不静默消失（ADR native-failure-surface Decision 2）
            HostLog.Write($"[host] 原生失败面承载失败，回落页内恢复面：{ex.Message}");
            await ShowPageFailureSurfaceAsync(host, reason).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _failureModalInFlight, 0);
        }
    }

    /// <summary>失败面「退出应用」动作：与托盘/恢复页退出同一条顺序契约——先批准关窗闸门再 Close（hide-to-tray 拦截下未批准的 Close 会吞成隐藏）。</summary>
    private void ExitFromFailureSurface()
    {
        HostLog.Write("[host] 用户在失败面选择退出应用");
        try
        {
            _app.App.Services.GetRequiredService<Tray.TrayController>().CloseGate.ApproveExit();
            _app.App.Services.GetRequiredService<IRynWindow>().Close();
        }
        catch (Exception ex)
        {
            HostLog.Write($"[host] 失败面退出动作失败：{ex.Message}");
        }
    }

    /// <summary>恢复回调（监督器重启给出新 URL）：重验铸币态（epoch 可能已换，token-free 路径；ADR
    /// mint-epoch-mux-gate）并解锁失败面闩。<b>零壳导航的前提是承载面不碰活动页 DOM</b>——原生框不碰即不导航
    /// （窗口恒驻代理根，回来交页内自刷与客户端原地重连）；回落页内恢复面覆写过 DOM，只有该分支补收口导航。
    /// </summary>
    /// <param name="url">重启后的 dsh 端点（仅供重验与日志；导航靶点恒为代理根）。</param>
    private ValueTask RevalidateAfterRecoveryAsync(Uri url)
    {
        _ = _shellForward.RevalidateAsync(DshWebUrl.From(url), HostLog.Write, CancellationToken.None).GetAwaiter().GetResult();
        Volatile.Write(ref _failureSurfaceShown, 0);
        Uri? target = _proxy?.Url;
        if (Interlocked.Exchange(ref _pageSurfaceFallbackShown, 0) == 0 || target is null)
        {
            HostLog.Write($"[nav] 恢复后重验铸币态（{url.GetLeftPart(UriPartial.Authority)}），不代发窗口导航");
            return ValueTask.CompletedTask;
        }

        HostLog.Write($"[nav] 回落承载面覆写过活动页，收口回代理根（{url.GetLeftPart(UriPartial.Authority)}）");
        return _app.WindowAccessor.Current.NavigateAsync(target);
    }

    private void SetupHealthMonitor(SupervisorSetup supervisor)
    {
        // 页面健康观测 + 有界恢复（ADR page-health-monitor / reference-alignment 批次五）：
        // 宿主只读探针轮询，不注入不依赖 companion——「dsh 在跑但页面空白」类事故（历史三起全靠
        // 人肉发现）从此有自动留痕；连续 Dead 达阈值后在预算内触发一次有界 reload，耗尽转观测-only，
        // 成功恢复复位预算（防误报引发无限重载循环，对齐参照 plugin_boot.rs 的有界刷新门控）。
        // 首拍延迟（HealthInitialDelaySeconds）避开启动空窗，探针异常按 Unknown 续跑。reload 委托
        // 恒为当前代理靶点（代理源恒定，未铸币时 holder 自 reload）；代理绑定失败时该窗口页面是
        // wwwroot 引导页（有内容 → Alive），不会进入 Dead 恢复分支——空态只是防御性兜底。
        PageHealthMonitor monitor = _app.App.Services.GetRequiredService<PageHealthMonitor>();
        _ = monitor.RunAsync(TimeSpan.FromSeconds(_timeouts.HealthInitialDelaySeconds), supervisor.Cts.Token);
    }

    /// <summary>无边框窗口 chrome 注入接线（ADR frameless-uniform-caption-bar「注入时机」条，该条按
    /// 注册／接线即注入／导航钩子编号，本方法按调用序排列）：<b>注册</b>走
    /// <see cref="PagePump.RegisterCaptionBarScriptWhenReadyAsync"/>——三路里唯一不靠导航事件覆盖后续每次加载的一路
    /// （holder 页 <c>location.reload()</c> 进真 UI 是同 URL 重载，mac/win 的导航回调不发；Linux 用户脚本
    /// 逐文档重放，故该端导航钩子也覆盖）；<b>接线即注入</b>覆盖「注册只对后续文档生效」的当窗空档；
    /// <b>常驻导航钩子</b>每次到达后重挂（Linux 每跳都发；三路共用同一份脚本——语言由脚本执行期按
    /// <c>html[lang]</c> 现取，构建期 locale 只是该属性缺失时的回落，故无需按到达重建）。
    /// 三路都幂等（脚本自带守卫）；失败仅留痕——窗口控制退路是托盘菜单的唤回/最大化/退出
    /// （hide-to-tray 关窗闸门不依赖注入）。</summary>
    private void SetupCaptionBar(SupervisorSetup supervisor)
    {
        CancellationToken ct = supervisor.Cts.Token;
        RynNavigationCallbacks callbacks = _app.App.Services.GetRequiredService<RynNavigationCallbacks>();
        // macChrome = 是否启用原生 macOS chrome（判据单点见 MacNativeChrome）。
        bool macChrome = MacNativeChrome.IsEnabled;
        string script = CaptionBar.Build(_captionBar.HeightPx, _uiLocale, macChrome);
        Func<IRynWebView> webView = () => _app.App.Services.GetRequiredService<IRynWebView>();
        callbacks.SetOnNavigatedPersistent(() => Task.Run(() =>
        {
            _ = PagePump.InjectCaptionBarWhenReadyAsync(_app.WindowAccessor, script, ct);
            // 首次到达补注册一次（该事件只在 webview 就绪后发）：到点预算折算出的注册之外再保一路「后到
            // 但确实活着」的注册时机；只补一次，避免每次导航都新增一条引擎注册。
            if (Interlocked.Exchange(ref _captionBarReregistered, 1) == 0)
            {
                _ = PagePump.RegisterCaptionBarScriptWhenReadyAsync(webView, script, ct);
            }
        }));
        _ = Task.Run(() => PagePump.InjectCaptionBarWhenReadyAsync(_app.WindowAccessor, script, ct));
        _ = Task.Run(() => PagePump.RegisterCaptionBarScriptWhenReadyAsync(webView, script, ct));
    }

    /// <summary>启动期告知任务接线（ADR shared-home-desktop-profile）：版本底线 + 旧 home 提示 +
    /// 脏退横幅的用例编排住 <see cref="StartupNoticeService"/>（Infrastructure）；此处只接线
    /// 展示面闭包（横幅构建与推送是 Presentation 面，与 UpdateCoordinator 的委托接线同型）。</summary>
    private void StartupNoticeTask(HostSetup host, SupervisorSetup supervisor)
    {
        var notices = new StartupNoticeService(
            _preflight.Bootstrap,
            TimeSpan.FromSeconds(_timeouts.BootstrapSettleTimeoutSeconds),
            host.Marker.PreviousRunUnclean,
            HostLog.Write,
            (version, ct) => PagePump.ShowBannerWhenReadyAsync(
                _app.WindowAccessor, DesktopBanner.BuildVersionFloorBanner(version, _uiLocale), ct),
            ct => PagePump.ShowBannerWhenReadyAsync(
                _app.WindowAccessor, DesktopBanner.BuildUncleanExitBanner(_uiLocale), ct));
        _ = Task.Run(() => notices.RunAsync(supervisor.Cts.Token));
    }

    /// <summary>引导完成后的铸币收尾：覆盖式重铸（本方法是 bootstrap 路径的 epoch 起点）。
    /// 不导航（鉴权自愈的同源重载除外）——窗口恒在代理源上，未铸币时 holder 自轮询就绪后自 reload
    /// （renderer 发起，绕开 saucer set_url 原生挂家族；见 ADR loopback-forward-proxy）。
    /// 由引导服务在 dsh 就位时回调。</summary>
    /// <param name="url">dsh 就位端点（仅供落定重铸与日志；无需导航，恒驻代理源）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task EnterMainUiAsync(DshWebUrl url, CancellationToken ct)
    {
        if (_proxy is null)
        {
            HostLog.Write("[nav] 进入主界面无代理源（回环绑定失败），本次不导航（dsh 已就绪，重启即进）");
            return;
        }

        // 壳铸币先行（不依赖窗口：纯 HTTP；holder 的就绪轮询以此次铸币为门——先铸才放行）。
        _ = await _shellForward.MintAsync(url, HostLog.Write, ct).ConfigureAwait(false);
        // 窗口可能尚未建好（原生建窗慢于 dsh 就位时，首个 Current 即抛，ADR bootstrap-window-ready-wait）：
        // 有界等可用，超时 loud 跳过本次（dsh 已就绪，holder 自 reload 即进）。
        if (!await WaitForWindowAsync(ct).ConfigureAwait(false))
        {
            HostLog.Write($"[nav] 等窗口可用超时（{_timeouts.WindowReadyTimeoutSeconds}s），跳过本次（holder 自 reload 即进）");
            return;
        }

        await SettleWebSessionAsync(url, ct);
    }
}
