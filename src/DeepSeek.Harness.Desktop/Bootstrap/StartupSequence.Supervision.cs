using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// <see cref="StartupSequence"/> 的监督与退出接线面：监督用例装配（引导落定门控 + 监督循环下沉 Core，
/// ADR 组合根机制收官 step-1 的 B2）、页面健康观测、无边框顶栏注入与启动期告知接线。
/// 失败面承载与恢复回调随监督用例住 Core（<c>Core.Bootstrap.RuntimeSupervision</c>）；
/// 启动主链在 <c>StartupSequence.cs</c>。
/// </summary>
internal sealed partial class StartupSequence
{
    private SupervisorSetup SetupSupervisor(HostSetup host)
    {
        // 监督器取消令牌源由组合根拥有（容器单例，寿命 = 本次 Run；释放随组合根作用域）。
        CancellationTokenSource cts = _app.App.Services.GetRequiredService<CancellationTokenSource>();
        // 引导落定门控、监督循环、失败面承载与恢复回调全在 Core 用例（ADR 组合根机制收官 step-1 的 B2）；
        // 宿主经阶段产出收参，其余依赖容器供给。返回的任务供退出路径 join。
        Core.Bootstrap.RuntimeSupervision supervision =
            _app.App.Services.GetRequiredService<Core.Bootstrap.RuntimeSupervision>();
        return new SupervisorSetup(cts, supervision.RunAsync(host.Host, cts.Token));
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
}
