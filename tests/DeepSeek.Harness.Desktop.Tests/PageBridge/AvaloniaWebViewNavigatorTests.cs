using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>Avalonia 网页视图导航端口（ADR avalonia-g1g3-recovery-bootstrap-supervision）：
/// 导航转发、到达信号只认成功完成、求值回报解壳、窗口就绪跟表面可用性——四契约都不依赖真实引擎。</summary>
public class AvaloniaWebViewNavigatorTests
{
    /// <summary>导航原样落到可测面（收口导航与鉴权自愈的靶点）。</summary>
    [Fact]
    public async Task NavigateAsync_ForwardsTargetToSurface()
    {
        var surface = new FakeWebViewSurface();
        using var navigator = new AvaloniaWebViewNavigator(surface);
        var target = new Uri("http://127.0.0.1:41449/");

        await navigator.NavigateAsync(target, CancellationToken.None);

        Assert.Equal([target], surface.Navigations);
    }

    /// <summary>到达信号只在导航成功完成时触发；失败完成不得解除提交等待。</summary>
    [Fact]
    public void ArrivalCallback_FiresOnlyOnSuccessfulCompletion()
    {
        var surface = new FakeWebViewSurface();
        using var navigator = new AvaloniaWebViewNavigator(surface);
        int arrived = 0;
        navigator.SetArrivalCallback(() => arrived++);

        surface.RaiseNavigationCompleted(new Uri("http://127.0.0.1:41449/"), isSuccess: false);
        Assert.Equal(0, arrived);

        surface.RaiseNavigationCompleted(new Uri("http://127.0.0.1:41449/"));
        Assert.Equal(1, arrived);

        navigator.SetArrivalCallback(static () => { });
        surface.RaiseNavigationCompleted(new Uri("http://127.0.0.1:41449/"));
        Assert.Equal(1, arrived);
    }

    /// <summary>求值回报按 JSON 字符串解壳（与 Ryn 端口同一解码点），探针读到的才是文本。</summary>
    [Fact]
    public async Task EvaluateScript_DecodesJsonStringEnvelope()
    {
        var surface = new FakeWebViewSurface { ScriptResult = "\"dsh\"" };
        using var navigator = new AvaloniaWebViewNavigator(surface);

        string? result = await navigator.EvaluateScriptAsync("document.title");

        Assert.Equal("dsh", result);
        Assert.Equal(["document.title"], surface.Scripts);
    }

    /// <summary>窗口就绪跟表面可用性：销毁后不得再报就绪（收口导航会立即打未就绪）。</summary>
    [Fact]
    public void IsWindowReady_TracksSurfaceAvailability()
    {
        var surface = new FakeWebViewSurface();
        using var navigator = new AvaloniaWebViewNavigator(surface);
        Assert.True(navigator.IsWindowReady);

        surface.IsAvailable = false;
        Assert.False(navigator.IsWindowReady);
    }

    /// <summary>惰性脚本目标：网页视图尚未挂载时求值按「未就绪」抛 InvalidOperationException
    /// （PagePump 的重试单点与恢复面据此判读），挂载后正常求值。</summary>
    [Fact]
    public async Task LazyScriptTarget_ThrowsNotReady_ThenEvaluates()
    {
        FakeWebViewSurface? surface = null;
        var target = new AvaloniaWindowScriptTarget(() => surface);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => target.EvaluateAsync("1+1", CancellationToken.None));

        surface = new FakeWebViewSurface { ScriptResult = "2" };
        Assert.Equal("2", await target.EvaluateAsync("1+1", CancellationToken.None));
    }
}
