namespace DeepSeek.Harness.Desktop.Core.Tests.Plugins;

/// <summary>ProfilePackageCheck 的就位判定（dependencies 与 dsh.profile.bundles 两处来源缺一不可）；
/// 原在 MarketInstallHelper 委托壳下覆盖，壳退役后随实现归 Core 测试（ADR clean-architecture 的 B2 摘壳）。</summary>
public class ProfilePackageCheckTests
{
    private static string WriteTempFile(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "profile-pkg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>验证 package.json 文件缺失时 IsBundleInstalled 判定未安装并返回 false，不抛异常。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(ProfilePackageCheck.IsBundleInstalled(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dshmarket"));
    }

    /// <summary>验证 dependencies 与 dsh.profile.bundles 同时含 dshmarket 时判定已安装并返回 true。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsTrue_WhenBothPresent()
    {
        string json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        string p = WriteTempFile(json);
        try { Assert.True(ProfilePackageCheck.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证 dshmarket 仅出现在 dependencies、未列进 bundles 时判定未安装。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenBundlesMissing()
    {
        string json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}
            """;
        string p = WriteTempFile(json);
        try { Assert.False(ProfilePackageCheck.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证 bundles 含 dshmarket 但 dependencies 为空时判定未安装，两处来源缺一不可。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenDepsMissing()
    {
        string json = """
            {"dependencies":{},"dsh":{"profile":{"bundles":["dshmarket"]}}}
            """;
        string p = WriteTempFile(json);
        try { Assert.False(ProfilePackageCheck.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证 package.json 内容不是合法 JSON 时按未安装处理（返回 false）而不抛异常。</summary>
    [Fact]
    public void IsBundleInstalled_ReturnsFalse_WhenInvalidJson()
    {
        string p = WriteTempFile("not json");
        try { Assert.False(ProfilePackageCheck.IsBundleInstalled(p, "dshmarket")); } finally { File.Delete(p); }
    }

    /// <summary>验证同一清单内各包判定互不干扰：已装的 dshmarket 判 true、未装的 companion 仍判 false。</summary>
    [Fact]
    public void IsBundleInstalled_PerPackage_Independent()
    {
        // 市场已就位、伴生未装：市场判定 true、伴生判定 false（互不误判）
        string json = """
            {"dependencies":{"dshmarket":"1.15.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","dshmarket"]}}}
            """;
        string p = WriteTempFile(json);
        try
        {
            Assert.True(ProfilePackageCheck.IsBundleInstalled(p, "dshmarket"));
            Assert.False(ProfilePackageCheck.IsBundleInstalled(p, "dsh-desktop-companion"));
        }
        finally { File.Delete(p); }
    }
}
