using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 引导页语言拉取命令（ADR ui-copy-bilingual-completion）：desktop.ui.getLocale 返回当前
/// UI 语言强类型帧，页面据此取中/英分支。
/// </summary>
public class UiLocaleCommandRouterTests
{
    /// <summary>验证帧形如 <c>{"locale":"en"}</c>（键名 = 属性名 CamelCase，页面直读 r.locale）。</summary>
    [Fact]
    public async Task Route_ReturnsLocaleFrame()
    {
        var uiLocale = new UiLocale();
        uiLocale.Set("en");
        var router = new UiLocaleCommandRouter(uiLocale);

        string json = await router.RouteAsync(UiLocaleCommandRouter.CommandName, ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("en", doc.RootElement.GetProperty("locale").GetString());
    }

    /// <summary>验证语言切换后帧随最新值（拉取时刻语义，非构造时刻快照）。</summary>
    [Fact]
    public async Task Route_ReflectsLatestLocale()
    {
        var uiLocale = new UiLocale();
        var router = new UiLocaleCommandRouter(uiLocale);

        uiLocale.Set("en");
        uiLocale.Set("zh-CN");

        string json = await router.RouteAsync(UiLocaleCommandRouter.CommandName, ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("zh-CN", doc.RootElement.GetProperty("locale").GetString());
    }

    /// <summary>验证只认自己的命令名，其余一律拒绝（IPC 路由契约）。</summary>
    [Fact]
    public void CanRoute_OnlyOwnCommand()
    {
        var router = new UiLocaleCommandRouter(new UiLocale());

        Assert.True(router.CanRoute(UiLocaleCommandRouter.CommandName));
        Assert.False(router.CanRoute("desktop.companion.setLocale"));
        Assert.Throws<Ryn.Ipc.RynCommandNotFoundException>(
            () => router.RouteAsync("desktop.other", ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None));
    }
}
