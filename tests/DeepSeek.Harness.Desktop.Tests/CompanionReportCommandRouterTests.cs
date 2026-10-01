using System.Text;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>页面降级报障路由契约（ADR companion-switch-degradation-recovery）：合法上报进 host.log
/// 单行；坏载荷/空载荷一律静默忽略——报障是增强能力，绝不制造第二个失败面。</summary>
public class CompanionReportCommandRouterTests
{
    private static ValueTask<string> RouteAsync(CompanionReportCommandRouter router, string json) =>
        router.RouteAsync(CompanionReportCommandRouter.CommandName,
            new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json)), null!, CancellationToken.None);

    /// <summary>验证路由器只认自己的命令名，其余一概拒绝（注册面与派发面同源）。</summary>
    [Fact]
    public void CanRoute_MatchesOnlyOwnCommand()
    {
        var router = new CompanionReportCommandRouter();
        Assert.True(router.CanRoute("desktop.companion.report"));
        Assert.False(router.CanRoute("desktop.companion.setLocale"));
        Assert.False(router.CanRoute("desktop.autostart.getState"));
    }

    /// <summary>合法上报：scope 与 message 原样进日志（前缀 [companion]），返回空对象帧。</summary>
    [Fact]
    public async Task ValidReport_LogsScopeAndMessage()
    {
        var lines = new List<string>();
        var router = new CompanionReportCommandRouter(lines.Add);

        string frame = await RouteAsync(router,
            """{"scope":"desktop.closeToTray.getState","message":"state unavailable after retry: IPC timeout"}""");

        Assert.Equal("{}", frame);
        string line = Assert.Single(lines);
        Assert.Contains("[companion]", line, StringComparison.Ordinal);
        Assert.Contains("desktop.closeToTray.getState", line, StringComparison.Ordinal);
        Assert.Contains("state unavailable after retry", line, StringComparison.Ordinal);
    }

    /// <summary>多行消息压成单行（含 U+2028/U+2029/U+0085）并按 MaxMessageLength 截断留省略号：
    /// 日志注入与刷屏都从此处挡住，上限按常量钉死。</summary>
    [Fact]
    public async Task MultilineMessage_FlattenedAndTruncated()
    {
        var lines = new List<string>();
        var router = new CompanionReportCommandRouter(lines.Add);

        // ① 全部行分隔符（\r\n 与 U+2028/U+2029/U+0085）折叠：一条上报恰落一行
        await RouteAsync(router, System.Text.Json.JsonSerializer.Serialize(
            new { scope = "scope", message = "first\n[host] 伪造的第二行\r\n\u2028\u2029\u0085tail" }));
        // ② 超长按常量截断：上限即 MaxMessageLength（不是「比输入短」这类弱断言）
        await RouteAsync(router, System.Text.Json.JsonSerializer.Serialize(
            new { scope = "scope", message = new string('x', CompanionReportCommandRouter.MaxMessageLength * 2) }));

        Assert.Equal(2, lines.Count);
        string flattened = lines[0];
        Assert.DoesNotContain('\n', flattened);
        Assert.DoesNotContain('\r', flattened);
        Assert.DoesNotContain('\u2028', flattened);
        Assert.DoesNotContain('\u2029', flattened);
        Assert.DoesNotContain('\u0085', flattened);
        Assert.Contains("first [host] 伪造的第二行", flattened, StringComparison.Ordinal);
        Assert.Contains("tail", flattened, StringComparison.Ordinal);

        string truncated = lines[1];
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
        Assert.Contains(new string('x', CompanionReportCommandRouter.MaxMessageLength), truncated, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', CompanionReportCommandRouter.MaxMessageLength + 1), truncated, StringComparison.Ordinal);
    }

    /// <summary>坏 JSON、空对象、非字符串字段、空 args 一律静默忽略：返回 null 帧且不产生日志。</summary>
    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"scope":123,"message":false}""")]
    [InlineData("""{"scope":"","message":"   "}""")]
    public async Task MalformedOrEmptyReport_IsIgnoredSilently(string json)
    {
        var lines = new List<string>();
        var router = new CompanionReportCommandRouter(lines.Add);

        Assert.Equal("null", await RouteAsync(router, json));
        Assert.Empty(lines);
    }

    /// <summary>空参数体（无 JSON）同样静默忽略——页面侧旧形态/坏帧不得让宿主抛 IPC 异常。</summary>
    [Fact]
    public async Task EmptyArgs_IsIgnoredSilently()
    {
        var lines = new List<string>();
        var router = new CompanionReportCommandRouter(lines.Add);

        string frame = await router.RouteAsync(CompanionReportCommandRouter.CommandName,
            ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None);

        Assert.Equal("null", frame);
        Assert.Empty(lines);
    }

    /// <summary>未知命令路由抛 RynCommandNotFoundException，绝不静默吞掉。</summary>
    [Fact]
    public async Task UnknownCommand_ThrowsNotFound()
    {
        var router = new CompanionReportCommandRouter();
        await Assert.ThrowsAsync<RynCommandNotFoundException>(() =>
            router.RouteAsync("desktop.companion.reportExtra", ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None)
                .AsTask());
    }
}
