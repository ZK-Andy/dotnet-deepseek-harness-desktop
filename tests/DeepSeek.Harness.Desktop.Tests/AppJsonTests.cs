using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>统一 JSON 通道（AppJsonContext 源生成）的帧形状与转义契约：
/// 错误帧精确形状、JS 字面量嵌值的 HTML 敏感字符转义边界。</summary>
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
    public void JsString_HtmlSensitiveEscaped_NoRawAngleBracket()
    {
        // 恢复页/横幅脚本嵌值的安全边界：裸 <script> 绝不以可执行形态出现在脚本字符串里
        var js = AppJsonContext.JsString("</script><script>alert(1)</script>");
        Assert.DoesNotContain('<', js);
        Assert.Contains("\\u003C", js);
        var parsed = JsonDocument.Parse(js).RootElement.GetString();
        Assert.Equal("</script><script>alert(1)</script>", parsed);
    }

    [Fact]
    public void JsString_NonAsciiEscaped_UppercaseHexForm()
    {
        // 默认编码器输出大写 \u 形态（断言别用小写）
        var js = AppJsonContext.JsString("运");
        Assert.DoesNotContain("运", js);
        Assert.Contains("\\u8FD0", js);
    }
}
