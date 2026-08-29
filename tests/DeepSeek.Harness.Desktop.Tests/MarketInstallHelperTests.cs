using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>MarketInstallHelper 的分支与错误路径覆盖，目标把 3.6% 拉至 40%+。</summary>
public class MarketInstallHelperTests
{
    private static string WriteTempFile(string content)
    {
        var p = Path.Combine(Path.GetTempPath(), "market-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(MarketInstallHelper.IsBundleInstalled(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
    }

    [Theory]
    [InlineData("file:/x/dshmarket.tgz", true)]
    [InlineData("FILE:/x/dshmarket.tgz", true)] // 大小写不敏感
    [InlineData("link:../dshmarket", true)]
    [InlineData("^1.36.0", false)]
    [InlineData("dshmarket@1.36.0", false)]
    [InlineData("npm:dshmarket@^1.0.0", false)]
    [InlineData("github:owner/repo", false)]
    [InlineData(null, false)]
    public void IsLocalSpec_ClassifiesSpecShape(string? spec, bool expected)
    {
        Assert.Equal(expected, MarketInstallHelper.IsLocalSpec(spec));
    }

    [Theory]
    [InlineData("/x/dshmarket.tgz", true)]
    [InlineData("C:\\x\\dshmarket.tgz", true)] // Windows 路径
    [InlineData("file:/x/dshmarket.tgz", true)]
    [InlineData("dshmarket", false)] // 裸包名（归化条目）
    [InlineData("dshmarket@1.36.0", false)] // registry 回退串
    [InlineData(null, false)]
    public void IsPathSpec_ClassifiesPendingSpecShape(string? spec, bool expected)
    {
        Assert.Equal(expected, MarketInstallHelper.IsPathSpec(spec));
    }

    [Fact]
    public void ReadDependencySpec_ReturnsRawSpec_WhenPresent()
    {
        var json = """{"dependencies":{"dshmarket":"file:/x/dshmarket.tgz","other":123}}""";
        var p = WriteTempFile(json);
        try
        {
            Assert.Equal("file:/x/dshmarket.tgz", MarketInstallHelper.ReadDependencySpec(p, "dshmarket"));
            Assert.Null(MarketInstallHelper.ReadDependencySpec(p, "other")); // 非字符串值按「形态未知」处理
            Assert.Null(MarketInstallHelper.ReadDependencySpec(p, "ghost")); // 未装
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ReadDependencySpec_ReturnsNull_WhenFileMissingOrInvalid()
    {
        Assert.Null(MarketInstallHelper.ReadDependencySpec(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
        var p = WriteTempFile("not json");
        try { Assert.Null(MarketInstallHelper.ReadDependencySpec(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_ReturnsTrue_WhenBothPresent()
    {
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.True(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenBundlesMissing()
    {
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenDepsMissing()
    {
        var json = """
            {"dependencies":{},"dsh":{"profile":{"bundles":["dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenInvalidJson()
    {
        var p = WriteTempFile("not json");
        try { Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsBundleInstalled_PerPackage_Independent()
    {
        // 市场已就位、伴生未装：市场判定 true、伴生判定 false（互不误判）
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try
        {
            Assert.True(MarketInstallHelper.IsBundleInstalled(p, "dshmarket"));
            Assert.False(MarketInstallHelper.IsBundleInstalled(p, "dsh-desktop-companion"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task CleanupBogusApp_RemovesOnlyAppWithTgz()
    {
        var json = """
            {"dependencies":{"app":"file:/tmp/dshmarket.tgz","keep":"1.0.0"},"dsh":{"profile":{"bundles":[]}}}
            """;
        var p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            var after = File.ReadAllText(p);
            Assert.DoesNotContain("\"app\"", after);
            Assert.Contains("\"keep\"", after);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task CleanupBogusApp_NoOp_WhenNoApp()
    {
        var json = """{"dependencies":{"keep":"1.0.0"}}""";
        var p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            Assert.Contains("keep", File.ReadAllText(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task CleanupBogusApp_NoOp_WhenAppNotTgz()
    {
        var json = """{"dependencies":{"app":"1.0.0"}}""";
        var p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            Assert.Contains("\"app\"", File.ReadAllText(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task CleanupBogusApp_WriteFormat_IndentedWithTrailingNewline()
    {
        // 写盘格式钉子：缩进 JSON + 尾部换行（与 dsh 自身写盘形态一致，防序列化实现更换后漂移）
        var json = """{"dependencies":{"app":"file:/tmp/dshmarket.tgz","keep":"1.0.0"}}""";
        var p = WriteTempFile(json);
        try
        {
            await MarketInstallHelper.CleanupBogusAppDependencyAsync(p);
            var after = File.ReadAllText(p);
            Assert.EndsWith("\n", after);
            Assert.Contains("\n  \"dependencies\"", after);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void EnsureWorkspaceAllowBuilds_ReplacesPlaceholderAndAddsEsbuild()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ws = Path.Combine(dir, "pnpm-workspace.yaml");
        File.WriteAllText(ws, "allowBuilds:\n  '@deepseek-ai/dsh-subprocess-local': set this to true or false\n  koffi: set this to true or false\n");
        try
        {
            MarketInstallHelper.EnsureWorkspaceAllowBuilds(ws);
            var t = File.ReadAllText(ws);
            Assert.Contains("esbuild: true", t);
            Assert.Contains("'@deepseek-ai/dsh-subprocess-local': true", t);
            Assert.DoesNotContain("set this to true or false", t);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnsureWorkspaceAllowBuilds_NoOp_WhenMissing()
    {
        MarketInstallHelper.EnsureWorkspaceAllowBuilds(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        // 不抛即通过
    }

    [Fact]
    public void ResolveMarketSpec_PrefersTgz_WhenLarge()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tgz = Path.Combine(dir, "dshmarket.tgz");
        File.WriteAllBytes(tgz, new byte[11 * 1024]);
        try
        {
            Assert.Equal(tgz, MarketInstallHelper.ResolveMarketSpec(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveMarketSpec_FallsBackToDir_WhenTgzSmall()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tgz = Path.Combine(dir, "dshmarket.tgz");
        File.WriteAllBytes(tgz, new byte[100]);
        var d = Path.Combine(dir, "node_modules", "dshmarket");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "package.json"), "{}");
        try
        {
            Assert.Equal(d, MarketInstallHelper.ResolveMarketSpec(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveMarketSpec_FallsBackToRegistry_WhenNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(BundledPluginCatalog.MarketRegistryFallback, MarketInstallHelper.ResolveMarketSpec(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveCompanionSpec_PrefersTgz_WhenLarge()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tgz = Path.Combine(dir, "dsh-desktop-companion.tgz");
        File.WriteAllBytes(tgz, new byte[2 * 1024]);
        try
        {
            Assert.Equal(tgz, MarketInstallHelper.ResolveCompanionSpec(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveCompanionSpec_FallsBackToDir_WhenTgzTiny()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tgz = Path.Combine(dir, "dsh-desktop-companion.tgz");
        File.WriteAllBytes(tgz, new byte[100]);
        var d = Path.Combine(dir, "node_modules", "dsh-desktop-companion");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "package.json"), "{}");
        try
        {
            Assert.Equal(d, MarketInstallHelper.ResolveCompanionSpec(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveCompanionSpec_ReturnsNull_WhenNothing()
    {
        // 伴生插件无 registry 回退：闭包未携带时返回 null（调用方跳过），而非字符串 spec
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(MarketInstallHelper.ResolveCompanionSpec(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task EnsureBundles_AddsWhenMissing()
    {
        var json = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""";
        var p = WriteTempFile(json);
        try
        {
            var added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dshmarket");
            Assert.True(added);
            Assert.Contains("dshmarket", File.ReadAllText(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task EnsureBundles_AppendsSecondPackage_KeepsFirst()
    {
        var json = """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}""";
        var p = WriteTempFile(json);
        try
        {
            var added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dsh-desktop-companion");
            Assert.True(added);
            var after = File.ReadAllText(p);
            Assert.Contains("dshmarket", after);
            Assert.Contains("dsh-desktop-companion", after);
            // 再补写一次应幂等
            Assert.False(await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dsh-desktop-companion"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task EnsureBundles_NoOpWhenPresent()
    {
        var json = """{"dsh":{"profile":{"bundles":["dshmarket"]}}}""";
        var p = WriteTempFile(json);
        try
        {
            var added = await MarketInstallHelper.EnsureBundlesContainsAsync(p, "dshmarket");
            Assert.False(added);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task EnsureBundles_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(await MarketInstallHelper.EnsureBundlesContainsAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
    }
}
