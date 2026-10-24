using System.Text.RegularExpressions;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// <c>window.__shell</c> 桥（ADR avalonia-g2-shell-bridge）的分发/校验/注入/外链契约：
/// 信封合法性与 nonce、顶层文档 origin 判据、命令分发与错误帧、导航后 nonce 轮换、失败导航的状态保留、
/// 事件推送脚本形状，以及外链拦截的取消/新窗口/失败提示。
/// </summary>
public class ShellBridgeTests
{
    private const string Origin = "http://127.0.0.1:41449";
    private static readonly Uri s_shellUri = new(Origin + "/");
    private static readonly Uri s_externalUri = new("https://example.com/x");

    /// <summary>顶栏注入随桥同批（隔离测试：导航完成后应有第二段含顶栏标记的脚本）。</summary>
    [Fact]
    public async Task Navigation_CaptionEnabled_InjectsCaptionScript()
    {
        var surface = new FakeWebViewSurface();
        var bridge = new ShellBridge(
            surface,
            () => Array.Empty<ICommandRouter>(),
            Origin,
            new FakeSystemBrowser(),
            macChrome: false,
            captionHeightPx: 52,
            _ => { },
            captionBarEnabled: () => true);
        bridge.Attach();

        surface.RaiseNavigationCompleted(s_shellUri);

        string? caption = await surface.WaitForScriptAfterAsync(
            0, s => s.Contains("dsh-desktop-caption-bar", StringComparison.Ordinal));
        Assert.NotNull(caption);
        Assert.Contains("window.__shell", caption!, StringComparison.Ordinal);
    }

    private static (ShellBridge Bridge, FakeWebViewSurface Surface, FakeSystemBrowser Browser) Create(
        params ICommandRouter[] routers)
    {
        var surface = new FakeWebViewSurface();
        var browser = new FakeSystemBrowser();
        var bridge = new ShellBridge(surface, () => routers, Origin, browser, macChrome: false, captionHeightPx: 52, _ => { });
        return (bridge, surface, browser);
    }

    /// <summary>导航到给定 URL（默认壳 origin），并从本次注入脚本里取 nonce（注入是 nonce 的唯一事实源）。</summary>
    /// <param name="surface">可测面。</param>
    /// <param name="url">目标 URL（null = 壳 origin）。</param>
    /// <returns>当前文档 nonce。</returns>
    private static async Task<string> NavigateAsync(FakeWebViewSurface surface, Uri? url = null)
    {
        int start = surface.Scripts.Count;
        surface.RaiseNavigationCompleted(url ?? s_shellUri);
        string? script = await surface.WaitForScriptAfterAsync(
            start, s => s.Contains("var N=\"", StringComparison.Ordinal));
        Assert.NotNull(script);
        Match match = Regex.Match(script!, "var N=\"([0-9A-F]+)\"");
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    /// <summary>合法信封经路由后按原 id 回包成功载荷。</summary>
    [Fact]
    public async Task Dispatch_RoutesCommandAndResolvesPayload()
    {
        var router = new FakeCommandRouter(HostCommandIds.UiGetLocale, _ => "{\"locale\":\"en\"}");
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create(router);
        bridge.Attach();
        string nonce = await NavigateAsync(surface);

        surface.RaiseMessage("{\"v\":1,\"n\":\"" + nonce + "\",\"id\":7,\"cmd\":\"" + HostCommandIds.UiGetLocale + "\",\"args\":{}}");

        string? script = await surface.WaitForScriptAsync(s => s.Contains("_resolve(7,true,", StringComparison.Ordinal));
        Assert.NotNull(script);
        Assert.Contains("{\"locale\":\"en\"}", script, StringComparison.Ordinal);
    }

    /// <summary>参数帧原文透传给路由（命令契约不做二次解包）。</summary>
    [Fact]
    public async Task Dispatch_PassesArgsFrameToRouter()
    {
        var router = new FakeCommandRouter(HostCommandIds.OpenExternal, _ => "{}");
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create(router);
        bridge.Attach();
        string nonce = await NavigateAsync(surface);

        surface.RaiseMessage("{\"v\":1,\"n\":\"" + nonce + "\",\"id\":1,\"cmd\":\"" + HostCommandIds.OpenExternal + "\",\"args\":{\"url\":\"https://a.test\"}}");

        Assert.NotNull(await surface.WaitForScriptAsync(s => s.Contains("_resolve(1,true,", StringComparison.Ordinal)));
        Assert.Equal("{\"url\":\"https://a.test\"}", router.ReceivedArgs[0]);
    }

    /// <summary>无路由认领的命令回错误帧（不抛、不断连）。</summary>
    [Fact]
    public async Task Dispatch_UnknownCommandResolvesError()
    {
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create();
        bridge.Attach();
        string nonce = await NavigateAsync(surface);

        surface.RaiseMessage("{\"v\":1,\"n\":\"" + nonce + "\",\"id\":2,\"cmd\":\"nope.missing\",\"args\":null}");

        string? script = await surface.WaitForScriptAsync(s => s.Contains("_resolve(2,false,", StringComparison.Ordinal));
        Assert.NotNull(script);
        // 错误帧经默认编码器全量转义（非 ASCII → \uXXXX，十六进制大写），故按转义形态断言。
        Assert.Contains("{\"error\":\"\\u672A\\u77E5", script, StringComparison.Ordinal);
    }

    /// <summary>路由抛异常时转错误帧，消息含异常文本。</summary>
    [Fact]
    public async Task Dispatch_RouterFailureResolvesError()
    {
        var router = new FakeCommandRouter(HostCommandIds.OpenExternal, failure: new InvalidOperationException("boom"));
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create(router);
        bridge.Attach();
        string nonce = await NavigateAsync(surface);

        surface.RaiseMessage("{\"v\":1,\"n\":\"" + nonce + "\",\"id\":3,\"cmd\":\"" + HostCommandIds.OpenExternal + "\",\"args\":null}");

        string? script = await surface.WaitForScriptAsync(s => s.Contains("_resolve(3,false,", StringComparison.Ordinal));
        Assert.NotNull(script);
        Assert.Contains("boom", script, StringComparison.Ordinal);
    }

    /// <summary>nonce 不匹配的消息被丢弃且不回包。</summary>
    [Fact]
    public async Task Dispatch_RejectsWrongNonce()
    {
        var router = new FakeCommandRouter(HostCommandIds.UiGetLocale, _ => "{}");
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create(router);
        bridge.Attach();
        _ = await NavigateAsync(surface);

        surface.RaiseMessage("{\"v\":1,\"n\":\"deadbeef\",\"id\":4,\"cmd\":\"" + HostCommandIds.UiGetLocale + "\",\"args\":{}}");
        await Task.Delay(200);

        Assert.DoesNotContain(surface.Scripts, s => s.Contains("_resolve(4,", StringComparison.Ordinal));
    }

    /// <summary>成功导航到壳 origin 之外后，携带新 nonce 的消息仍在顶层 origin 判据被丢弃（纵深防御；
    /// 生产上外站导航会被 NavigationStarted 取消，此分支无常规可达路径）。</summary>
    [Fact]
    public async Task Dispatch_RejectsMessagesAfterNavigationLeavesShellOrigin()
    {
        var router = new FakeCommandRouter(HostCommandIds.UiGetLocale, _ => "{}");
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create(router);
        bridge.Attach();
        _ = await NavigateAsync(surface);

        string nonce = await NavigateAsync(surface, s_externalUri);

        surface.RaiseMessage("{\"v\":1,\"n\":\"" + nonce + "\",\"id\":10,\"cmd\":\"" + HostCommandIds.UiGetLocale + "\",\"args\":{}}");
        await Task.Delay(200);

        Assert.DoesNotContain(surface.Scripts, x => x.Contains("_resolve(10,", StringComparison.Ordinal));
    }

    /// <summary>壳 origin 自身加载失败（非外链取消）必须带 URL 留痕，否则 host.log 对这类失败零行。</summary>
    [Fact]
    public void FailedShellNavigation_LogsUrl()
    {
        var surface = new FakeWebViewSurface();
        var logs = new List<string>();
        var bridge = new ShellBridge(
            surface, () => Array.Empty<ICommandRouter>(), Origin, new FakeSystemBrowser(), macChrome: false, captionHeightPx: 52, logs.Add);
        bridge.Attach();

        surface.RaiseNavigationCompleted(new Uri(Origin + "/index.html"), isSuccess: false);

        Assert.Contains(logs, l => l.Contains("导航未完成", StringComparison.Ordinal) && l.Contains("/index.html", StringComparison.Ordinal));
    }

    /// <summary>URL 未知的失败也不能静默吞掉。</summary>
    [Fact]
    public void FailedNavigationWithoutUrl_LogsPlaceholder()
    {
        var surface = new FakeWebViewSurface();
        var logs = new List<string>();
        var bridge = new ShellBridge(
            surface, () => Array.Empty<ICommandRouter>(), Origin, new FakeSystemBrowser(), macChrome: false, captionHeightPx: 52, logs.Add);
        bridge.Attach();

        surface.RaiseNavigationCompleted(null, isSuccess: false);

        Assert.Contains(logs, l => l.Contains("URL 未知", StringComparison.Ordinal));
    }

    /// <summary>外链失败属 NavigationStarted 取消的预期路径，不逐次留痕。</summary>
    [Fact]
    public void FailedExternalNavigation_IsSilent()
    {
        var surface = new FakeWebViewSurface();
        var logs = new List<string>();
        var bridge = new ShellBridge(
            surface, () => Array.Empty<ICommandRouter>(), Origin, new FakeSystemBrowser(), macChrome: false, captionHeightPx: 52, logs.Add);
        bridge.Attach();

        surface.RaiseNavigationCompleted(s_externalUri, isSuccess: false);

        Assert.DoesNotContain(logs, l => l.Contains("导航未完成", StringComparison.Ordinal));
    }

    /// <summary>失败/取消的导航（生产上外站导航必被 NavigationStarted 取消）不换 nonce、不改 origin，
    /// 仍在显示的文档继续可 invoke。</summary>
    [Fact]
    public async Task FailedNavigation_KeepsDocumentNonceAndOrigin()
    {
        var router = new FakeCommandRouter(HostCommandIds.UiGetLocale, _ => "{}");
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create(router);
        bridge.Attach();
        string nonce = await NavigateAsync(surface);

        surface.RaiseNavigationCompleted(s_externalUri, isSuccess: false);
        await Task.Delay(50);

        surface.RaiseMessage("{\"v\":1,\"n\":\"" + nonce + "\",\"id\":9,\"cmd\":\"" + HostCommandIds.UiGetLocale + "\",\"args\":{}}");
        Assert.NotNull(await surface.WaitForScriptAsync(s => s.Contains("_resolve(9,true,", StringComparison.Ordinal)));
    }

    /// <summary>每次导航轮换 nonce，旧文档的 nonce 随即失效。</summary>
    [Fact]
    public async Task Navigation_RotatesNonceAndRejectsStaleOne()
    {
        var router = new FakeCommandRouter(HostCommandIds.UiGetLocale, _ => "{}");
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create(router);
        bridge.Attach();
        string first = await NavigateAsync(surface);
        string second = await NavigateAsync(surface);

        Assert.NotEqual(first, second);

        surface.RaiseMessage("{\"v\":1,\"n\":\"" + first + "\",\"id\":6,\"cmd\":\"" + HostCommandIds.UiGetLocale + "\",\"args\":{}}");
        await Task.Delay(200);
        Assert.DoesNotContain(surface.Scripts, s => s.Contains("_resolve(6,", StringComparison.Ordinal));
    }

    /// <summary>事件推送脚本带事件名与 JSON 载荷。</summary>
    [Fact]
    public async Task EmitEvent_WritesEmitScriptWithJsonPayload()
    {
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create();
        bridge.Attach();

        bridge.EmitEvent("tray.clicked", "{\"a\":1}");

        string? script = await surface.WaitForScriptAsync(
            s => s.Contains("_emit(\"tray.clicked\"", StringComparison.Ordinal));
        Assert.NotNull(script);
        Assert.Contains("{\"a\":1}", script, StringComparison.Ordinal);
    }

    /// <summary>外站 http(s) 导航被取消并交系统浏览器。</summary>
    [Fact]
    public void ExternalNavigation_IsCancelledAndOpenedInBrowser()
    {
        (ShellBridge bridge, FakeWebViewSurface surface, FakeSystemBrowser browser) = Create();
        bridge.Attach();

        PageNavigationEventArgs args = surface.RaiseNavigationStarted(s_externalUri);

        Assert.True(args.Cancel);
        Assert.Equal(new[] { s_externalUri.ToString() }, browser.Opened);
    }

    /// <summary>同源导航放行且不碰浏览器。</summary>
    [Fact]
    public void SameOriginNavigation_IsAllowed()
    {
        (ShellBridge bridge, FakeWebViewSurface surface, FakeSystemBrowser browser) = Create();
        bridge.Attach();

        PageNavigationEventArgs args = surface.RaiseNavigationStarted(new Uri(Origin + "/index.html"));

        Assert.False(args.Cancel);
        Assert.Empty(browser.Opened);
    }

    /// <summary>外链打开失败推 ExternalLinkOpenerFailed 事件。</summary>
    [Fact]
    public async Task ExternalNavigation_OpeningFailurePublishesEvent()
    {
        var surface = new FakeWebViewSurface();
        var browser = new FakeSystemBrowser(result: false);
        var bridge = new ShellBridge(surface, () => Array.Empty<ICommandRouter>(), Origin, browser, macChrome: false, captionHeightPx: 52, _ => { });
        bridge.Attach();

        _ = surface.RaiseNavigationStarted(s_externalUri);

        string? script = await surface.WaitForScriptAsync(
            s => s.Contains("_emit(\"desktop.externalLinkOpenerFailed\"", StringComparison.Ordinal));
        Assert.NotNull(script);
        Assert.Contains("example.com", script, StringComparison.Ordinal);
    }

    /// <summary>新窗口请求标记已处理并交系统浏览器。</summary>
    [Fact]
    public void NewWindowRequest_IsHandledAndOpenedExternally()
    {
        (ShellBridge bridge, FakeWebViewSurface surface, FakeSystemBrowser browser) = Create();
        bridge.Attach();

        PageNavigationEventArgs args = surface.RaiseNewWindowRequested(s_externalUri);

        Assert.True(args.Handled);
        Assert.Equal(new[] { s_externalUri.ToString() }, browser.Opened);
    }

    /// <summary>非 http(s) scheme（mailto 等）放行给页面。</summary>
    [Fact]
    public void NonHttpNavigation_IsAllowed()
    {
        (ShellBridge bridge, FakeWebViewSurface surface, _) = Create();
        bridge.Attach();

        PageNavigationEventArgs args = surface.RaiseNavigationStarted(new Uri("mailto:a@b.test"));

        Assert.False(args.Cancel);
    }
}
