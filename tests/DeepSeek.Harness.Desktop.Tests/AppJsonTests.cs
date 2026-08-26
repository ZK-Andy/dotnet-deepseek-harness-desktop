using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>统一 JSON 通道（AppJsonContext 源生成）的帧形状与转义契约：
/// 错误帧精确形状、autostart 状态帧线形 pin、JS 字面量嵌值的转义边界。</summary>
public class AppJsonTests
{
    [Fact]
    public void Error_ExactCompactShape()
    {
        Assert.Equal("""{"error":"boom"}""", AppJsonContext.Error("boom"));
    }

    [Fact]
    public void Error_HostileMessage_RoundTrips()
    {
        const string hostile = "引号\"与反斜杠\\与<script>";
        var parsed = JsonDocument.Parse(AppJsonContext.Error(hostile)).RootElement;
        Assert.Equal(hostile, parsed.GetProperty("error").GetString());
    }

    [Fact]
    public void AutostartState_ExactShape()
    {
        // 帧线形 pin：姊妹路由均有精确串断言（CloseToTrayTests），此为 autostart 唯一线形钉
        Assert.Equal("""{"enabled":true}""",
            JsonSerializer.Serialize(new AutostartCommandRouter.StateFrame(true), AppJsonContext.Default.AutostartState));
        Assert.Equal("""{"enabled":false}""",
            JsonSerializer.Serialize(new AutostartCommandRouter.StateFrame(false), AppJsonContext.Default.AutostartState));
    }

    [Fact]
    public void JsString_HtmlSensitiveAndNonAsciiEscaped_RoundTrips()
    {
        // 恢复页/横幅脚本嵌值的安全边界：裸 <script> 绝不以可执行形态出现在脚本字符串里；
        // 默认编码器输出大写 \u 形态（断言别用小写）
        var js = AppJsonContext.JsString("</script>运");
        Assert.DoesNotContain('<', js);
        Assert.Contains("\\u003C", js);
        Assert.Contains("\\u8FD0", js);
        Assert.Equal("</script>运", JsonDocument.Parse(js).RootElement.GetString());
    }
}
