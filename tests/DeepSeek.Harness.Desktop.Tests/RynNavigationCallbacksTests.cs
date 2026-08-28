using System;
using System.Collections.Generic;
using DeepSeek.Harness.Desktop.Services;
using Ryn.Core;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>宿主导航回调（Ryn.Callbacks）：导航层外部链接拦截 + 页面到达信号。</summary>
public class RynNavigationCallbacksTests
{
    private static WebViewNavigatingContext Navigating(string url, bool newWindow = false, bool redirect = false, bool userInitiated = true) =>
        new(new Uri(url), newWindow, redirect, userInitiated);

    /// <summary>站外绝对 http(s) → Block + 交给打开器。</summary>
    [Theory]
    [InlineData("https://x.example/article")]
    [InlineData("http://external.test/")]
    [InlineData("https://other.host:8443/path")]
    public void Navigating_ExternalHttp_BlocksAndOpens(string url)
    {
        var opened = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: u => { opened.Add(u); return true; },
            log: null,
            currentOrigin: "http://127.0.0.1:41449");

        var decision = handler.OnWebViewNavigating(Navigating(url));

        Assert.Equal(NavigationDecision.Block, decision);
        Assert.Single(opened);
        Assert.Equal(url, opened[0]);
    }

    /// <summary>同源绝对 http → Allow（SPA 内部导航），不交给打开器。</summary>
    [Theory]
    [InlineData("http://127.0.0.1:41449/app")]
    [InlineData("http://127.0.0.1:41449/settings")]
    public void Navigating_SameOriginHttp_Allows(string url)
    {
        var opened = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: u => { opened.Add(u); return true; },
            log: null,
            currentOrigin: "http://127.0.0.1:41449");

        var decision = handler.OnWebViewNavigating(Navigating(url));

        Assert.Equal(NavigationDecision.Allow, decision);
        Assert.Empty(opened);
    }

    /// <summary>非 http(s) scheme / 相对资源 → Allow（放行 ryn://、data:、SPA 内部锚点）。</summary>
    [Theory]
    [InlineData("ryn://x")]
    [InlineData("data:text/html,hello")]
    [InlineData("javascript:void(0)")]
    public void Navigating_NonHttpScheme_Allows(string url)
    {
        var opened = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: u => { opened.Add(u); return true; },
            log: null,
            currentOrigin: "http://127.0.0.1:41449");

        var decision = handler.OnWebViewNavigating(Navigating(url));

        Assert.Equal(NavigationDecision.Allow, decision);
        Assert.Empty(opened);
    }

    /// <summary>打开器抛异常 → 仍 Block（避免 WebView 被带走），不向调用方外抛。</summary>
    [Fact]
    public void Navigating_OpenerThrows_StillBlocks()
    {
        var handler = new RynNavigationCallbacks(
            opener: _ => throw new InvalidOperationException("no browser"),
            log: null,
            currentOrigin: "http://127.0.0.1:41449");

        var decision = handler.OnWebViewNavigating(Navigating("https://x.example/"));

        Assert.Equal(NavigationDecision.Block, decision);
    }

    /// <summary>无 currentOrigin（null）→ 保守把一切绝对 http(s) 视为外部并 Block。</summary>
    [Fact]
    public void Navigating_NoOrigin_BlocksAnyAbsoluteHttp()
    {
        var opened = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: u => { opened.Add(u); return true; },
            log: null,
            currentOrigin: null);

        var decision = handler.OnWebViewNavigating(Navigating("https://x.example/"));

        Assert.Equal(NavigationDecision.Block, decision);
        Assert.Single(opened);
    }

    /// <summary>WebViewNavigated：触发「页面已到达」回调。</summary>
    [Fact]
    public void Navigated_InvokesOnNavigatedCallback()
    {
        var calls = 0;
        var handler = new RynNavigationCallbacks();
        handler.SetOnNavigated(() => calls++);

        handler.OnWebViewNavigated(new WebViewNavigatedContext(new Uri("http://127.0.0.1:41449/")));

        Assert.Equal(1, calls);
    }

    /// <summary>宿主导航（IsUserInitiated=false）→ 一律 Allow：崩溃恢复 NavigateAsync 不被误拦（B1）。</summary>
    [Theory]
    [InlineData("https://x.example/", "http://127.0.0.1:41449")]
    [InlineData("http://127.0.0.1:9999/app", "http://127.0.0.1:41449")]
    public void Navigating_HostInitiated_AlwaysAllows(string url, string origin)
    {
        var opened = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: u => { opened.Add(u); return true; },
            log: null,
            currentOrigin: origin);

        // IsUserInitiated=false：宿主程序化导航（恢复/重定向）放行，即使目标是站外或漂移后的端口
        var decision = handler.OnWebViewNavigating(Navigating(url, userInitiated: false));

        Assert.Equal(NavigationDecision.Allow, decision);
        Assert.Empty(opened);
    }

    /// <summary>origin 随 WebViewNavigated 刷新（B1）：漂移到新 origin 后，新 origin 的同源 SPA 链接放行。</summary>
    [Fact]
    public void Navigating_OriginRefreshesOnNavigated()
    {
        var opened = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: u => { opened.Add(u); return true; },
            log: null,
            currentOrigin: "http://127.0.0.1:41449");

        // 初始 origin 是旧端口；崩溃恢复导航到新 origin（端口漂移）
        handler.OnWebViewNavigated(new WebViewNavigatedContext(new Uri("http://127.0.0.1:9999/")));

        // 新 origin 上的同源链接 → 放行（不再是外部）
        var decision = handler.OnWebViewNavigating(Navigating("http://127.0.0.1:9999/app"));

        Assert.Equal(NavigationDecision.Allow, decision);
        Assert.Empty(opened);
    }

    /// <summary>opener 返回 false（非抛异常）→ 打开失败通知触发（R2 N2 toast）。</summary>
    [Fact]
    public void Navigating_OpenerReturnsFalse_NotifiesLinkFail()
    {
        var notified = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: _ => false,
            log: null,
            currentOrigin: "http://127.0.0.1:41449",
            notifyLinkFail: url => notified.Add(url));

        var decision = handler.OnWebViewNavigating(Navigating("https://x.example/"));

        Assert.Equal(NavigationDecision.Block, decision);
        Assert.Single(notified);
        Assert.Equal("https://x.example/", notified[0]);
    }

    /// <summary>opener 抛异常 → 打开失败通知触发（R2 N2 toast），不向调用方外抛。</summary>
    [Fact]
    public void Navigating_OpenerThrows_NotifiesLinkFail()
    {
        var notified = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: _ => throw new InvalidOperationException("no browser"),
            log: null,
            currentOrigin: "http://127.0.0.1:41449",
            notifyLinkFail: url => notified.Add(url));

        var decision = handler.OnWebViewNavigating(Navigating("https://x.example/"));

        Assert.Equal(NavigationDecision.Block, decision);
        Assert.Single(notified);
    }

    /// <summary>opener 成功 → 不触发打开失败通知（正常路径零打扰）。</summary>
    [Fact]
    public void Navigating_OpenerSucceeds_DoesNotNotifyLinkFail()
    {
        var notified = new List<string>();
        var handler = new RynNavigationCallbacks(
            opener: _ => true,
            log: null,
            currentOrigin: "http://127.0.0.1:41449",
            notifyLinkFail: url => notified.Add(url));

        handler.OnWebViewNavigating(Navigating("https://x.example/"));

        Assert.Empty(notified);
    }
}
