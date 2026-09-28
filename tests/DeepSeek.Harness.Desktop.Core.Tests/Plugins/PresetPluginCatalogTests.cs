namespace DeepSeek.Harness.Desktop.Core.Tests.Plugins;

/// <summary>preset 插件首启待装判定（profile package.json 解析）——原与 shell 侧路由/帧测试同文件，
/// 按「测谁归谁」拆归 Core.Tests（ADR post-packaging-churn-restructure 余批 G）。</summary>
public class PresetPluginCatalogTests
{
    private static string WriteProfileJson(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "preinstall-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>验证 profile 文件缺失时首启待装列表为 dshmarket（按全新环境处理）而非报错。</summary>
    [Fact]
    public void Pending_ReturnsMarket_WhenFileMissing()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Equal(["dshmarket"], PresetPluginCatalog.PendingForFirstBoot(missing));
    }

    /// <summary>验证 dependencies 与 bundles 均未含 dshmarket 时判定其为待装插件。</summary>
    [Fact]
    public void Pending_ReturnsMarket_WhenNotInstalled()
    {
        string p = WriteProfileJson("""{"dependencies":{},"dsh":{"profile":{"bundles":["web-app"]}}}""");
        try
        {
            Assert.Equal(["dshmarket"], PresetPluginCatalog.PendingForFirstBoot(p));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证 dependencies 与 bundles 均已就位 dshmarket 时首启待装列表为空。</summary>
    [Fact]
    public void Pending_ReturnsEmpty_WhenMarketInstalled()
    {
        string p = WriteProfileJson("""{"dependencies":{"dshmarket":"^1.36.0"},"dsh":{"profile":{"bundles":["dshmarket","web-app"]}}}""");
        try
        {
            Assert.Empty(PresetPluginCatalog.PendingForFirstBoot(p));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证「registry 已装但 bundles 未补写」的异常路径按未就位处理，引导页重新呈现 dshmarket。</summary>
    [Fact]
    public void Pending_ReturnsMarket_WhenDepButNoBundle()
    {
        // registry 安装后 bundles 未补写（异常路径）：按未就位处理，引导页重新呈现
        string p = WriteProfileJson("""{"dependencies":{"dshmarket":"^1.36.0"},"dsh":{"profile":{"bundles":["web-app"]}}}""");
        try
        {
            Assert.Equal(["dshmarket"], PresetPluginCatalog.PendingForFirstBoot(p));
        }
        finally { File.Delete(p); }
    }
}
