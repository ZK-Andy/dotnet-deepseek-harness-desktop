using System.Text;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>locale 桥接路由契约（ADR host-ui-locale）：合法上报更新单点并触发事件；
/// 坏 JSON/缺字段/非法形态一律静默忽略（增强能力，绝不报 IPC 错）。</summary>
public class CompanionLocaleCommandRouterTests
{
    private static ValueTask<string> Route(CompanionLocaleCommandRouter router, string json) =>
        router.RouteAsync(CompanionLocaleCommandRouter.CommandName,
            new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json)), null!, CancellationToken.None);

    [Fact]
    public async Task ValidLocale_UpdatesUiLocale()
    {
        var ui = new UiLocale();
        int fired = 0;
        ui.Changed += () => fired++;
        var router = new CompanionLocaleCommandRouter(ui);

        Assert.Equal("{}", await Route(router, """{"locale":"en"}"""));

        Assert.Equal("en", ui.Current);
        Assert.Equal(1, fired);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"locale":123}""")]
    [InlineData("""{}""")]
    [InlineData("""{"locale":""}""")]
    [InlineData("""{"locale":"not a locale!"}""")]
    [InlineData("""{"locale":"e"}""")]
    public async Task InvalidPayload_Ignored_ReturnsNull(string json)
    {
        var ui = new UiLocale();
        string before = ui.Current;
        var router = new CompanionLocaleCommandRouter(ui);

        Assert.Equal("null", await Route(router, json));
        Assert.Equal(before, ui.Current);
    }

    [Fact]
    public async Task SameLocale_NoDuplicateEvent()
    {
        var ui = new UiLocale();
        ui.Set("en");
        int fired = 0;
        ui.Changed += () => fired++;
        var router = new CompanionLocaleCommandRouter(ui);

        await Route(router, """{"locale":"en"}""");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void CanRoute_OnlyOwnCommand()
    {
        var router = new CompanionLocaleCommandRouter(new UiLocale());
        Assert.True(router.CanRoute(CompanionLocaleCommandRouter.CommandName));
        Assert.False(router.CanRoute("desktop.update.getState"));
    }
}
