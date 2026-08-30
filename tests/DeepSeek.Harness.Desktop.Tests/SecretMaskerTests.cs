using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>日志脱敏分层契约（ADR diag-masking-and-recovery-page）：各层命中、组合与无误伤。</summary>
public class SecretMaskerTests
{
    /// <summary>验证 Cookie / set-cookie / Authorization 头行整值（含 Bearer token）被掩码为 *** 并保留头名。</summary>
    [Theory]
    [InlineData("Cookie: session=abcdef0123456789; theme=dark", "Cookie: ***")]
    [InlineData("set-cookie: sid=x", "set-cookie: ***")]
    [InlineData("Authorization: Bearer sk-abc123", "Authorization: ***")]
    public void HeaderLines_MaskEntireValue(string input, string expected)
    {
        Assert.Equal(expected, SecretMasker.Mask(input));
    }

    /// <summary>验证 URL 查询串中 secret 形键名的取值被掩码为 ***，键名与其余查询结构原样保留。</summary>
    [Theory]
    [InlineData("https://api.x.com/v1/res?token=abcdef123456&n=1", "https://api.x.com/v1/res?token=***&n=1")]
    [InlineData("GET /a?access_token=xyz&tab=i HTTP/1.1", "GET /a?access_token=***&tab=i HTTP/1.1")]
    [InlineData("redirect?SECRET=topsecret&ok", "redirect?SECRET=***&ok")]
    public void UrlSecretKeys_MaskValueOnly_KeepStructure(string input, string expected)
    {
        Assert.Equal(expected, SecretMasker.Mask(input));
    }

    /// <summary>验证 JSON 键值对中引号包裹的 secret 值被掩码为 ***，键名与引号外壳原样保留。</summary>
    [Theory]
    [InlineData("{\"apiKey\": \"sk-verysecretvalue\"}", "{\"apiKey\": \"***\"}")]
    [InlineData("password: 'hunter2!'", "password: '***'")]
    public void QuotedAssignments_MaskValue(string input, string expected)
    {
        Assert.Equal(expected, SecretMasker.Mask(input));
    }

    /// <summary>验证 sk- 前缀与长 hex 摘要等裸 token 形态按形态回退掩码，不依赖键名上下文。</summary>
    [Theory]
    [InlineData("key=sk-Abc12345Defghi789 done", "key=sk-*** done")]
    [InlineData("digest aabbccdd11223344aabbccdd11223344 end", "digest *** end")]
    public void BareTokenShapes_MaskWithFallback(string input, string expected)
    {
        Assert.Equal(expected, SecretMasker.Mask(input));
    }

    /// <summary>验证不含 secret 的常规日志行（URL、路径、插件输出）原样透传，确认掩码无误伤。</summary>
    [Theory]
    [InlineData("[2026-08-26 01:20:00] [host] dsh web = http://127.0.0.1:46777")]
    [InlineData("路径 /home/user/.dsh/logs/host.log 滚动完成 (5MB)")]
    [InlineData("plugin add exit=0 bundles=[dshmarket,dsh-desktop-companion]")]
    public void BenignLines_PassThroughUnchanged(string input)
    {
        Assert.Equal(input, SecretMasker.Mask(input));
    }

    /// <summary>验证同一行内 URL token、头值与引号赋值各脱敏层同时生效，敏感片段全部消失。</summary>
    [Fact]
    public void Combined_Line_AllLayersApply()
    {
        string? masked = SecretMasker.Mask("POST /auth?token=aaaa1111bbbb2222 Cookie: sid=z; key=\"apiKey\":\"sk-x12345678\"");
        Assert.DoesNotContain("aaaa1111", masked);
        Assert.DoesNotContain("sid=z", masked);
        Assert.DoesNotContain("sk-x12345678", masked);
    }

    /// <summary>验证 null 输入脱敏后透传返回 null 且不抛异常。</summary>
    [Fact]
    public void Null_PassesThrough()
    {
        Assert.Null(SecretMasker.Mask(null));
    }
}
