using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>资产挑选（expanded_assets hrefs → RID+包类型匹配）、包类型检测、SHA256SUMS 解析。</summary>
public class ReleaseAssetTests
{
    private const string Repo = "ZK-Andy/dotnet-deepseek-harness-desktop";

    [Fact]
    public void Pick_LinuxAmd64_Deb()
    {
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            $"/{Repo}/releases/download/v0.1.21/app_0.1.21_linux-amd64.deb",
            $"/{Repo}/releases/download/v0.1.21/app_0.1.21_linux-arm64.deb",
            $"/{Repo}/releases/download/v0.1.21/SHA256SUMS.txt",
        ], "linux-x64", Repo, UpdatePlatform.Deb);

        Assert.NotNull(meta);
        Assert.Equal("app_0.1.21_linux-amd64.deb", meta.AssetName);
        Assert.EndsWith("/SHA256SUMS.txt", meta.Sha256Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Pick_LinuxX64_Rpm_UsesRpmArchName()
    {
        // rpm 系统必须拿 x86_64.rpm（架构命名与 deb 的 amd64 不同）——2026-08-22 实机串包教训
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            $"/{Repo}/releases/download/v0.1.21/app_0.1.21_linux-amd64.deb",
            $"/{Repo}/releases/download/v0.1.21/app_0.1.21_linux-x86_64.rpm",
            $"/{Repo}/releases/download/v0.1.21/SHA256SUMS.txt",
        ], "linux-x64", Repo, UpdatePlatform.Rpm);

        Assert.NotNull(meta);
        Assert.Equal("app_0.1.21_linux-x86_64.rpm", meta.AssetName);
    }

    [Theory]
    [InlineData("win-x64", null, "app_0.1.21_windows-x64-setup.exe")]
    [InlineData("linux-arm64", "deb", "app_0.1.21_linux-arm64.deb")]
    [InlineData("linux-arm64", "rpm", "app_0.1.21_linux-aarch64.rpm")]
    [InlineData("osx-arm64", null, "app_0.1.21_macos-arm64.dmg")]
    public void Pick_PerRid(string rid, string? kind, string expectedName)
    {
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            $"/{Repo}/releases/download/v0.1.21/{expectedName}",
            $"/{Repo}/releases/download/v0.1.21/SHA256SUMS.txt",
        ], rid, Repo, kind);

        Assert.NotNull(meta);
        Assert.Equal(expectedName, meta.AssetName);
    }

    [Fact]
    public void Pick_ReturnsNull_WhenNoMatchingAsset()
    {
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            $"/{Repo}/releases/download/v0.1.21/SHA256SUMS.txt",
        ], "win-x64", Repo);

        Assert.Null(meta);
    }

    [Fact]
    public void DetectPackageKind_PrefersDpkg_FallsBackRpm()
    {
        Assert.Equal(UpdatePlatform.Deb, UpdatePlatform.DetectPackageKind(hasDpkg: true, hasRpm: false));
        Assert.Equal(UpdatePlatform.Deb, UpdatePlatform.DetectPackageKind(hasDpkg: true, hasRpm: true));
        Assert.Equal(UpdatePlatform.Rpm, UpdatePlatform.DetectPackageKind(hasDpkg: false, hasRpm: true));
        // 两者皆无回退 deb：装不上时错误信息仍可诊断
        Assert.Equal(UpdatePlatform.Deb, UpdatePlatform.DetectPackageKind(hasDpkg: false, hasRpm: false));
    }

    [Fact]
    public void ParseSha256_HandlesStandardAndBinaryFormats()
    {
        string sums = "abc123def456abc123def456abc123def456abc123def456abc123def456abc1  app_0.1.20_linux-amd64.deb\n" +
                   "fff000fff000fff000fff000fff000fff000fff000fff000fff000fff000fff1 *app_0.1.20_windows-x64-setup.exe\n" +
                   "short other-file.deb";
        Assert.Equal("abc123def456abc123def456abc123def456abc123def456abc123def456abc1",
            InstallerDownloader.ParseSha256(sums, "app_0.1.20_linux-amd64.deb"));
        Assert.Equal("fff000fff000fff000fff000fff000fff000fff000fff000fff000fff000fff1",
            InstallerDownloader.ParseSha256(sums, "app_0.1.20_windows-x64-setup.exe"));
        Assert.Null(InstallerDownloader.ParseSha256(sums, "missing.deb"));
    }

    [Fact]
    public void DownloadLock_ExclusiveAcrossInstances()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using FileStream? first = InstallerDownloader.TryAcquireDownloadLock(dir);
            Assert.NotNull(first);
            // 第二实例拿不到锁（防双写 .part 损坏）
            Assert.Null(InstallerDownloader.TryAcquireDownloadLock(dir));
            // 释放后可再次获取（进程死亡自动释放的语义等价）
            first?.Dispose();
            using FileStream? second = InstallerDownloader.TryAcquireDownloadLock(dir);
            Assert.NotNull(second);
        }
        finally { Directory.Delete(dir, true); }
    }
}
