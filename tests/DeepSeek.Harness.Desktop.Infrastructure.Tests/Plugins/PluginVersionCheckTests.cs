namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Plugins;

/// <summary>插件包元数据读取与版本判定的边界：tgz/目录/已装副本三种来源 + 纯判定错误路径。
/// 读盘在 Infrastructure 的 <see cref="PluginPackageReader"/>（ADR core-purity-boundaries），
/// 解析与判定在 Core 的 <see cref="PluginVersionCheck"/>。</summary>
public class PluginVersionCheckTests
{
    private static readonly PluginPackageReader s_reader = new();

    /// <summary>内存构造 gzip+tar 包：自命名临时路径，条目构造委托给共享 <see cref="TestTarGz"/>。</summary>
    private static string WriteTgz(params (string EntryName, string Content)[] entries)
    {
        string p = Path.Combine(Path.GetTempPath(), "pvc-" + Guid.NewGuid().ToString("N") + ".tgz");
        TestTarGz.Write(p, entries);
        return p;
    }

    /// <summary>验证从 tgz 的 package/package.json 读出版本号 0.0.2。</summary>
    [Fact]
    public void ReadBundledPackageJson_FromTgz_ReturnsVersion()
    {
        string p = WriteTgz(("package/package.json", """{"name":"dsh-desktop-companion","version":"0.0.2"}"""));
        try
        {
            string? json = s_reader.ReadBundledPackageJson(p);
            Assert.Equal("0.0.2", PluginVersionCheck.ReadVersion(json!));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证忽略非 package.json 条目与 ./ 目录前缀，仍能读出版本号。</summary>
    [Fact]
    public void ReadBundledPackageJson_FromTgz_IgnoresOtherEntriesAndDotSlashPrefix()
    {
        string p = WriteTgz(
            ("package/lib/index.js", "export {};"),
            ("./package/package.json", """{"version":"1.2.3"}"""));
        try
        {
            string? json = s_reader.ReadBundledPackageJson(p);
            Assert.Equal("1.2.3", PluginVersionCheck.ReadVersion(json!));
        }
        finally { File.Delete(p); }
    }

    /// <summary>验证 tgz 内没有 package.json 条目时抛 InvalidDataException。</summary>
    [Fact]
    public void ReadBundledPackageJson_TgzWithoutPackageJson_Throws()
    {
        string p = WriteTgz(("package/lib/index.js", "export {};"));
        try { Assert.Throws<InvalidDataException>(() => s_reader.ReadBundledPackageJson(p)); } finally { File.Delete(p); }
    }

    /// <summary>验证 gzip 数据损坏时抛异常而非静默返回。</summary>
    [Fact]
    public void ReadBundledPackageJson_CorruptTgz_Throws()
    {
        string p = Path.Combine(Path.GetTempPath(), "pvc-bad-" + Guid.NewGuid().ToString("N") + ".tgz");
        File.WriteAllBytes(p, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        try { Assert.ThrowsAny<Exception>(() => s_reader.ReadBundledPackageJson(p)); } finally { File.Delete(p); }
    }

    /// <summary>验证 tgz 文件不存在时抛 FileNotFoundException。</summary>
    [Fact]
    public void ReadBundledPackageJson_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => s_reader.ReadBundledPackageJson(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tgz")));
    }

    /// <summary>验证 version 字段为数字、缺失或清单非 JSON 等坏字段形态在解析侧即失败（不静默）。</summary>
    [Theory]
    [InlineData("""{"version":123}""")]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"version":""}""")]
    [InlineData("not json")]
    public void ReadVersion_BadVersionField_Throws(string json)
    {
        string p = WriteTgz(("package/package.json", json));
        try
        {
            string? text = s_reader.ReadBundledPackageJson(p);
            Assert.ThrowsAny<Exception>(() => PluginVersionCheck.ReadVersion(text!));
        }
        finally
        {
            File.Delete(p);
        }
    }

    /// <summary>验证目录形态（非 tgz）直接从目录下 package.json 读出版本。</summary>
    [Fact]
    public void ReadBundledPackageJson_DirectoryForm_ReadsPackageJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pvc-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), """{"version":"0.3.0"}""");
            string? json = s_reader.ReadBundledPackageJson(dir);
            Assert.Equal("0.3.0", PluginVersionCheck.ReadVersion(json!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewProfileWithInstalledPlugin(string? versionJson)
    {
        string profileDir = Path.Combine(Path.GetTempPath(), "pvc-prof-" + Guid.NewGuid().ToString("N"));
        string pkgDir = Path.Combine(profileDir, "node_modules", "dsh-desktop-companion");
        Directory.CreateDirectory(pkgDir);
        if (versionJson is not null)
        {
            File.WriteAllText(Path.Combine(pkgDir, "package.json"), versionJson);
        }

        return profileDir;
    }

    /// <summary>验证从 profile 的 node_modules 已装副本 package.json 读出版本号。</summary>
    [Fact]
    public void ReadInstalledPackageJson_InstalledCopy_ReturnsVersion()
    {
        string dir = NewProfileWithInstalledPlugin("""{"version":"0.0.1"}""");
        try
        {
            string? json = s_reader.ReadInstalledPackageJson(dir, "dsh-desktop-companion");
            Assert.Equal("0.0.1", PluginVersionCheck.TryReadVersion(json!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证已装副本无 version 字段或清单损坏时按版本未知处理（null → 走升级/重装）。</summary>
    [Theory]
    [InlineData((string?)null)]
    [InlineData("""{"name":"dsh-desktop-companion"}""")]
    [InlineData("corrupt json")]
    public void ReadInstalledPackageJson_UnknownOrBroken_ReturnsNull(string? json)
    {
        string dir = NewProfileWithInstalledPlugin(json);
        try
        {
            string? text = s_reader.ReadInstalledPackageJson(dir, "dsh-desktop-companion");
            Assert.Null(text is null ? null : PluginVersionCheck.TryReadVersion(text));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证 profile 目录不存在时不抛异常、返回 null。</summary>
    [Fact]
    public void ReadInstalledPackageJson_MissingProfile_ReturnsNull()
    {
        Assert.Null(s_reader.ReadInstalledPackageJson(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dsh-desktop-companion"));
    }

    /// <summary>验证安装版本低于捆绑版本才需要升级，相等或更高不需要，安装版本为 null 时按需要升级。</summary>
    [Theory]
    [InlineData(null, "0.0.1", true)]
    [InlineData("0.0.1", "0.0.2", true)]
    [InlineData("0.0.9", "0.1.0", true)]
    [InlineData("0.0.1", "0.0.1", false)]
    [InlineData("0.1.0", "0.0.9", false)]
    public void NeedsUpgrade_VersionCompare(string? installed, string bundled, bool expected)
    {
        Assert.Equal(expected, PluginVersionCheck.NeedsUpgrade(installed, bundled));
    }

    /// <summary>验证版本段含非数字（0.a.3）时抛 ArgumentException，失败不静默。</summary>
    [Fact]
    public void NeedsUpgrade_UnparseableSegment_FailsLoud()
    {
        Assert.Throws<ArgumentException>(() => PluginVersionCheck.NeedsUpgrade("0.a.3", "0.1.0"));
    }
}
