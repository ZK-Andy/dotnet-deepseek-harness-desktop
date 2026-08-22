using DeepSeek.Harness.Desktop.Services.Update;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>资产挑选（expanded_assets hrefs → RID 匹配）与 SHA256SUMS 解析。</summary>
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
        ], "linux-x64", Repo);

        Assert.NotNull(meta);
        Assert.Equal("app_0.1.21_linux-amd64.deb", meta.AssetName);
        Assert.EndsWith("/SHA256SUMS.txt", meta.Sha256Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win-x64", "app_0.1.21_windows-x64-setup.exe")]
    [InlineData("linux-arm64", "app_0.1.21_linux-arm64.deb")]
    [InlineData("osx-arm64", "app_0.1.21_macos-arm64.dmg")]
    public void Pick_PerRid(string rid, string expectedName)
    {
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            $"/{Repo}/releases/download/v0.1.21/{expectedName}",
            $"/{Repo}/releases/download/v0.1.21/SHA256SUMS.txt",
        ], rid, Repo);

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
    public void ParseSha256_HandlesStandardAndBinaryFormats()
    {
        var sums = "abc123def456abc123def456abc123def456abc123def456abc123def456abc1  app_0.1.20_linux-amd64.deb\n" +
                   "fff000fff000fff000fff000fff000fff000fff000fff000fff000fff000fff1 *app_0.1.20_windows-x64-setup.exe\n" +
                   "short other-file.deb";
        Assert.Equal("abc123def456abc123def456abc123def456abc123def456abc123def456abc1",
            InstallerDownloader.ParseSha256(sums, "app_0.1.20_linux-amd64.deb"));
        Assert.Equal("fff000fff000fff000fff000fff000fff000fff000fff000fff000fff000fff1",
            InstallerDownloader.ParseSha256(sums, "app_0.1.20_windows-x64-setup.exe"));
        Assert.Null(InstallerDownloader.ParseSha256(sums, "missing.deb"));
    }
}
