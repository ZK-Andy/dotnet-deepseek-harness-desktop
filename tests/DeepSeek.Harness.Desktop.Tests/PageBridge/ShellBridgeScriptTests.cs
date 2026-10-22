using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// 注入脚本与信封协议的纯面契约（ADR avalonia-g2-shell-bridge）：脚本含 nonce/顶栏数据/回调名，
/// 信封解析按字段逐项拒绝，回包/事件脚本只在载荷合法 JSON 时原样内嵌。
/// </summary>
public class ShellBridgeScriptTests
{
    /// <summary>注入脚本携带 nonce、桥回调与顶栏数据。</summary>
    [Fact]
    public void Build_CarriesNonceAndTitlebar()
    {
        string script = ShellBridgeScript.Build("abc123", macChrome: true, captionHeightPx: 52);

        Assert.Contains("\"abc123\"", script, StringComparison.Ordinal);
        Assert.Contains("window.__shell=", script, StringComparison.Ordinal);
        Assert.Contains("_resolve", script, StringComparison.Ordinal);
        Assert.Contains("_emit", script, StringComparison.Ordinal);
        Assert.Contains("invokeCSharpAction", script, StringComparison.Ordinal);
        Assert.Contains("post({v:" + ShellBridgeProtocol.Version, script, StringComparison.Ordinal);
        Assert.Contains("__shell_titlebar={mac:true,height:52}", script, StringComparison.Ordinal);
    }

    /// <summary>非 macOS 形态的顶栏数据。</summary>
    [Fact]
    public void Build_MarksNonMacTitlebar()
    {
        string script = ShellBridgeScript.Build("n", macChrome: false, captionHeightPx: 44);

        Assert.Contains("__shell_titlebar={mac:false,height:44}", script, StringComparison.Ordinal);
    }

    /// <summary>合法 JSON 载荷原样内嵌为表达式。</summary>
    [Fact]
    public void ResolveScript_EmbedsPayloadAsExpression()
    {
        string script = ShellBridgeProtocol.ResolveScript(9, ok: true, "{\"locale\":\"en\"}");

        Assert.Contains("_resolve(9,true,{\"locale\":\"en\"})", script, StringComparison.Ordinal);
    }

    /// <summary>非法 JSON 载荷按字符串编码，不拼裸文本。</summary>
    [Fact]
    public void ResolveScript_EncodesIllegalPayloadAsString()
    {
        string script = ShellBridgeProtocol.ResolveScript(9, ok: false, "not json");

        Assert.Contains("_resolve(9,false,\"not json\")", script, StringComparison.Ordinal);
    }

    /// <summary>事件名按 JSON 字符串编码。</summary>
    [Fact]
    public void EmitScript_QuotesEventName()
    {
        string script = ShellBridgeProtocol.EmitScript("tray.clicked", "null");

        Assert.Contains("_emit(\"tray.clicked\",null)", script, StringComparison.Ordinal);
    }

    /// <summary>合法信封解析出 id/命令/参数帧。</summary>
    [Fact]
    public void TryParse_AcceptsValidEnvelope()
    {
        bool ok = ShellBridgeProtocol.TryParse(
            "{\"v\":1,\"n\":\"N\",\"id\":5,\"cmd\":\"" + HostCommandIds.OpenExternal + "\",\"args\":{\"url\":\"https://x\"}}",
            "N",
            out long id,
            out string command,
            out string argsJson);

        Assert.True(ok);
        Assert.Equal(5, id);
        Assert.Equal(HostCommandIds.OpenExternal, command);
        Assert.Equal("{\"url\":\"https://x\"}", argsJson);
    }

    /// <summary>缺字段、版本不符、nonce 不符或非 JSON 一律拒绝。</summary>
    [Theory]
    [InlineData("{\"v\":2,\"n\":\"N\",\"id\":1,\"cmd\":\"x\",\"args\":null}")]
    [InlineData("{\"v\":1,\"n\":\"M\",\"id\":1,\"cmd\":\"x\",\"args\":null}")]
    [InlineData("{\"v\":1,\"n\":\"N\",\"cmd\":\"x\",\"args\":null}")]
    [InlineData("{\"v\":1,\"n\":\"N\",\"id\":1,\"cmd\":\"\",\"args\":null}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsMalformedEnvelope(string? body)
    {
        Assert.False(ShellBridgeProtocol.TryParse(body, "N", out _, out _, out _));
    }

    /// <summary>缺 args 字段按 null 帧处理。</summary>
    [Fact]
    public void TryParse_DefaultsMissingArgsToNull()
    {
        Assert.True(ShellBridgeProtocol.TryParse(
            "{\"v\":1,\"n\":\"N\",\"id\":1,\"cmd\":\"x\"}", "N", out _, out _, out string argsJson));
        Assert.Equal("null", argsJson);
    }

    /// <summary>求值回报直通，null 映射为空串。</summary>
    [Fact]
    public async Task AvaloniaWindowScriptTarget_PassesThroughAndMapsNullToEmpty()
    {
        var surface = new FakeWebViewSurface { ScriptResult = "\"text:42\"" };
        var target = new AvaloniaWindowScriptTarget(surface);

        Assert.Equal("\"text:42\"", await target.EvaluateAsync("PROBE", CancellationToken.None));

        surface.ScriptResult = null;
        Assert.Equal(string.Empty, await target.EvaluateAsync("PROBE", CancellationToken.None));
    }

    /// <summary>未就绪的 InvalidOperationException 原样上抛。</summary>
    [Fact]
    public async Task AvaloniaWindowScriptTarget_PropagatesNotReady()
    {
        var surface = new FakeWebViewSurface();
        surface.Enqueue(new InvalidOperationException("not ready"));
        var target = new AvaloniaWindowScriptTarget(surface);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => target.EvaluateAsync("PROBE", CancellationToken.None));
    }
}
