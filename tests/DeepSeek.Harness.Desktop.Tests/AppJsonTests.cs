using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>统一 JSON 通道（AppJsonContext 源生成）的帧形状与转义契约：
/// 错误帧精确形状、autostart 状态帧线形 pin、JS 字面量嵌值的转义边界。</summary>
public class AppJsonTests
{
    /// <summary>验证 Error 输出的错误帧与紧凑形态线形 {"error":"boom"} 逐字符一致。</summary>
    [Fact]
    public void Error_ExactCompactShape()
    {
        Assert.Equal("""{"error":"boom"}""", AppJsonContext.Error("boom"));
    }

    /// <summary>验证含引号、反斜杠与 HTML 敏感标签字符的恶意消息经 Error 转义后可无损往返还原。</summary>
    [Fact]
    public void Error_HostileMessage_RoundTrips()
    {
        const string hostile = "引号\"与反斜杠\\与<script>";
        JsonElement parsed = JsonDocument.Parse(AppJsonContext.Error(hostile)).RootElement;
        Assert.Equal(hostile, parsed.GetProperty("error").GetString());
    }

    /// <summary>验证 autostart 状态帧 enabled true/false 两态经 AppJsonContext 序列化后逐字符命中紧凑线形。</summary>
    [Fact]
    public void AutostartState_ExactShape()
    {
        // 帧线形 pin：姊妹路由均有精确串断言（CloseToTrayTests），此为 autostart 唯一线形钉
        Assert.Equal("""{"enabled":true}""",
            JsonSerializer.Serialize(new AutostartCommandRouter.StateFrame(true), AppJsonContext.Default.AutostartState));
        Assert.Equal("""{"enabled":false}""",
            JsonSerializer.Serialize(new AutostartCommandRouter.StateFrame(false), AppJsonContext.Default.AutostartState));
    }

    /// <summary>验证 JsString 对 HTML 敏感字符与中文一律转义为 \u 形态（大写），且转义后仍可解析还原原值。</summary>
    [Fact]
    public void JsString_HtmlSensitiveAndNonAsciiEscaped_RoundTrips()
    {
        // 恢复页/横幅脚本嵌值的安全边界：裸 <script> 绝不以可执行形态出现在脚本字符串里；
        // 默认编码器输出大写 \u 形态（断言别用小写）
        string js = AppJsonContext.JsString("</script>运");
        Assert.DoesNotContain('<', js);
        Assert.Contains("\\u003C", js);
        Assert.Contains("\\u8FD0", js);
        Assert.Equal("</script>运", JsonDocument.Parse(js).RootElement.GetString());
    }
}
