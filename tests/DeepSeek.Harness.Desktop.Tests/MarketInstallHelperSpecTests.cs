using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

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
    public void IsValidRegistrySpec_RejectsInjectionAndLocalShapes(string spec)
    {
        Assert.False(MarketInstallHelper.IsValidRegistrySpec(spec, out string reason));
        Assert.NotEmpty(reason);
    }

    /// <summary>验证 registry 来源的非法 spec 在拼参数前被拒：执行器一次都不跑，且日志留原因。</summary>
    [Fact]
    public async Task RunPluginAddAsync_RegistryMalformed_RejectsWithoutSpawning()
    {
        bool ran = false;
        var logs = new List<string>();

        (int exit, _, string err) = await MarketInstallHelper.RunPluginAddAsync(
            "node",
            "/dsh/bin.js",
            "/home/u/.dsh",
            "--malicious",
            MarketInstallHelper.PluginSpecOrigin.Registry,
            logs.Add,
            (psi, ct) =>
            {
                ran = true;
                return Task.FromResult((0, string.Empty, string.Empty));
            },
            CancellationToken.None);

        Assert.False(ran, "非法 registry spec 不得触达子进程");
        Assert.NotEqual(0, exit);
        Assert.NotEmpty(err);
        Assert.Contains(logs, l => l.Contains("拒绝不合法的市场插件 spec"));
    }

    /// <summary>验证随包来源的 <c>file:</c> spec 不受 registry allowlist 约束——安装目录可含空格（Windows）。</summary>
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
            MarketInstallHelper.PluginSpecOrigin.Bundled,
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
