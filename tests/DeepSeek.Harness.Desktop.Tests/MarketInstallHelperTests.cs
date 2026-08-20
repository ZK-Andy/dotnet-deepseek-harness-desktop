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
    public void IsMarketInstalled_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(MarketInstallHelper.IsMarketInstalled(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void IsMarketInstalled_ReturnsTrue_WhenBothPresent()
    {
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.True(MarketInstallHelper.IsMarketInstalled(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsMarketInstalled_ReturnsFalse_WhenBundlesMissing()
    {
        var json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsMarketInstalled(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsMarketInstalled_ReturnsFalse_WhenDepsMissing()
    {
        var json = """
            {"dependencies":{},"dsh":{"profile":{"bundles":["dshmarket"]}}}
            """;
        var p = WriteTempFile(json);
        try { Assert.False(MarketInstallHelper.IsMarketInstalled(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void IsMarketInstalled_ReturnsFalse_WhenInvalidJson()
    {
        var p = WriteTempFile("not json");
        try { Assert.False(MarketInstallHelper.IsMarketInstalled(p)); } finally { File.Delete(p); }
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
            Assert.Equal("dshmarket@1.15.0", MarketInstallHelper.ResolveMarketSpec(dir));
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
            var added = await MarketInstallHelper.EnsureBundlesContainsMarketAsync(p);
            Assert.True(added);
            Assert.Contains("dshmarket", File.ReadAllText(p));
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
            var added = await MarketInstallHelper.EnsureBundlesContainsMarketAsync(p);
            Assert.False(added);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task EnsureBundles_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(await MarketInstallHelper.EnsureBundlesContainsMarketAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
