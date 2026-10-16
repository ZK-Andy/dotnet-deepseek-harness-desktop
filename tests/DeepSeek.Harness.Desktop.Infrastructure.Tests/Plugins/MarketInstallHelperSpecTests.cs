
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Plugins;

/// <summary>市场 registry spec 形状 allowlist（ADR spawn-env-and-plugin-spec-hardening）与
/// <c>dsh plugin add</c> 安装路径的接线。</summary>
public class MarketInstallHelperSpecTests
{
    /// <summary>验证合法 registry 形状被接受：裸名、name@版本/tag、scoped 两种。</summary>
    /// <param name="spec">待校验 spec。</param>
    [Theory]
    [InlineData("dshmarket")]
    [InlineData("dshmarket@latest")]
    [InlineData("dshmarket@1.45.0")]
    [InlineData("noogenesis-dsh@0.2.2")]
    [InlineData("pkg@next")]
    [InlineData("@scope/name")]
    [InlineData("@deepseek-ai/dsh-mcp-client@0.1.1-rc.2")]
    [InlineData("@deepseek-ai/dsh@next")]
    public void IsValidRegistrySpec_AcceptsRegistryShapes(string spec) =>
        Assert.True(MarketInstallHelper.IsValidRegistrySpec(spec, out string reason), reason);

    /// <summary>验证注入面与本地路径形状被拒：空/前导 -/空白/反斜杠/URL 方案/file:·link:/scoped 缺斜杠/大小写与版本形状不合。</summary>
    /// <param name="spec">待校验 spec。</param>
    [Theory]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("a b")]
    [InlineData("a\\b")]
    [InlineData("https://evil.example/pkg")]
    [InlineData("file:/tmp/a.tgz")]
    [InlineData("FILE:/tmp/a.tgz")]
    [InlineData("link:../x")]
    [InlineData("@scope")]
    [InlineData("@scope/name@")]
    [InlineData("UPPER")]
    [InlineData("@Scope/name")]
    [InlineData("pkg@1.0.0 beta")]
    [InlineData("dshmarket@^1.2.3")]
    [InlineData("@scope/name@^0.1.1")]
    [InlineData("pkg@^latest")]
    public void IsValidRegistrySpec_RejectsInjectionAndLocalShapes(string spec)
    {
        Assert.False(MarketInstallHelper.IsValidRegistrySpec(spec, out string reason));
        Assert.NotEmpty(reason);
    }

    /// <summary>验证 caret 前缀被显式拒绝（非顺带形状错误）：原因点名 ERR_PNPM_SPEC_NOT_SUPPORTED（竞品教训钉子）。</summary>
    [Fact]
    public void IsValidRegistrySpec_RejectsCaretWithNamedReason()
    {
        Assert.False(MarketInstallHelper.IsValidRegistrySpec("dshmarket@^1.2.3", out string reason));
        Assert.Contains("ERR_PNPM_SPEC_NOT_SUPPORTED", reason);
    }

    /// <summary>验证随包 <c>file:</c> spec 原样进参数表——安装目录可含空格（Windows）不被改写。</summary>
    [Fact]
    public async Task RunPluginAddAsync_BundledFileSpecWithSpaces_Allowed()
    {
        const string spec = "file:/opt/My Apps/dsh-desktop-companion.tgz";
        var args = new List<string>();
        bool ran = false;

        (int exit, _, _) = await MarketInstallHelper.RunPluginAddAsync(
            "node",
            "/dsh/bin.js",
            "/home/u/.dsh",
            spec,
            _ => { },
            (psi, ct) =>
            {
                ran = true;
                args.Clear();
                foreach (string a in psi.ArgumentList)
                {
                    args.Add(a);
                }

                return Task.FromResult((0, "installed", string.Empty));
            },
            CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(0, exit);
        Assert.Contains(spec, args);
    }
}
