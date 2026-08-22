using System.Formats.Tar;
using System.IO.Compression;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>PluginVersionCheck 的边界与错误路径：tgz/目录/已装副本三种版本来源 + 升级判定。</summary>
public class PluginVersionCheckTests
{
    /// <summary>内存构造 gzip+tar 包（与 bundle-runtime-ci.sh 的 `tar -czf … package` 结构一致）。</summary>
    private static string WriteTgz(params (string EntryName, string Content)[] entries)
    {
        var p = Path.Combine(Path.GetTempPath(), "pvc-" + Guid.NewGuid().ToString("N") + ".tgz");
        using (var fs = File.Create(p))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        using (var writer = new TarWriter(gz))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                entry.DataStream = new MemoryStream(bytes);
                writer.WriteEntry(entry);
            }
        }

        return p;
    }

    [Fact]
    public void ReadBundledVersion_FromTgz_ReturnsVersion()
    {
        var p = WriteTgz(("package/package.json", """{"name":"dsh-desktop-companion","version":"0.0.2"}"""));
        try { Assert.Equal("0.0.2", PluginVersionCheck.ReadBundledVersion(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void ReadBundledVersion_FromTgz_IgnoresOtherEntriesAndDotSlashPrefix()
    {
        var p = WriteTgz(
            ("package/lib/index.js", "export {};"),
            ("./package/package.json", """{"version":"1.2.3"}"""));
        try { Assert.Equal("1.2.3", PluginVersionCheck.ReadBundledVersion(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void ReadBundledVersion_TgzWithoutPackageJson_Throws()
    {
        var p = WriteTgz(("package/lib/index.js", "export {};"));
        try { Assert.Throws<InvalidDataException>(() => PluginVersionCheck.ReadBundledVersion(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void ReadBundledVersion_CorruptTgz_Throws()
    {
        var p = Path.Combine(Path.GetTempPath(), "pvc-bad-" + Guid.NewGuid().ToString("N") + ".tgz");
        File.WriteAllBytes(p, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        try { Assert.ThrowsAny<Exception>(() => PluginVersionCheck.ReadBundledVersion(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void ReadBundledVersion_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => PluginVersionCheck.ReadBundledVersion(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tgz")));
    }

    [Theory]
    [InlineData("""{"version":123}""")]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"version":""}""")]
    [InlineData("not json")]
    public void ReadBundledVersion_BadVersionField_Throws(string json)
    {
        var p = WriteTgz(("package/package.json", json));
        try
        {
            Assert.ThrowsAny<Exception>(() => PluginVersionCheck.ReadBundledVersion(p));
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void ReadBundledVersion_DirectoryForm_ReadsPackageJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pvc-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), """{"version":"0.3.0"}""");
            Assert.Equal("0.3.0", PluginVersionCheck.ReadBundledVersion(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewProfileWithInstalledPlugin(string? versionJson)
    {
        var profileDir = Path.Combine(Path.GetTempPath(), "pvc-prof-" + Guid.NewGuid().ToString("N"));
        var pkgDir = Path.Combine(profileDir, "node_modules", "dsh-desktop-companion");
        Directory.CreateDirectory(pkgDir);
        if (versionJson is not null)
        {
            File.WriteAllText(Path.Combine(pkgDir, "package.json"), versionJson);
        }

        return profileDir;
    }

    [Fact]
    public void ReadInstalledVersion_InstalledCopy_ReturnsVersion()
    {
        var dir = NewProfileWithInstalledPlugin("""{"version":"0.0.1"}""");
        try
        {
            Assert.Equal("0.0.1", PluginVersionCheck.ReadInstalledVersion(dir, "dsh-desktop-companion"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("""{"name":"dsh-desktop-companion"}""")]
    [InlineData("corrupt json")]
    public void ReadInstalledVersion_UnknownOrBroken_ReturnsNull(string? json)
    {
        var dir = NewProfileWithInstalledPlugin(json);
        try
        {
            Assert.Null(PluginVersionCheck.ReadInstalledVersion(dir, "dsh-desktop-companion"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadInstalledVersion_MissingProfile_ReturnsNull()
    {
        Assert.Null(PluginVersionCheck.ReadInstalledVersion(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "dsh-desktop-companion"));
    }

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

    [Fact]
    public void NeedsUpgrade_UnparseableSegment_FailsLoud()
    {
        Assert.Throws<ArgumentException>(() => PluginVersionCheck.NeedsUpgrade("0.a.3", "0.1.0"));
    }
}
