using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>资产挑选（expanded_assets hrefs → RID+包类型匹配）、包类型检测、SHA256SUMS 解析。</summary>
public class ReleaseAssetTests
{
    private const string Repo = "ZK-Andy/dotnet-deepseek-harness-desktop";

    /// <summary>验证 linux-x64 + Deb 平台从 expanded_assets 挑中 linux-amd64.deb，且 SHA256SUMS 链接指向仓库内 sums 文件。</summary>
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

    /// <summary>验证 rpm 系统挑中 linux-x86_64.rpm——架构命名与 deb 的 amd64 不同，防实机串包（2026-08-22 教训）。</summary>
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

    /// <summary>验证各 RID（win-x64/linux-arm64/osx-arm64）与可选包类型组合挑中对应命名资产，含 linux-arm64 的 rpm 用 aarch64 命名特例。</summary>
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

    /// <summary>验证资产列表无可匹配项（仅有 SHA256SUMS）时返回 null，绝不退回错误资产。</summary>
    [Fact]
    public void Pick_ReturnsNull_WhenNoMatchingAsset()
    {
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            $"/{Repo}/releases/download/v0.1.21/SHA256SUMS.txt",
        ], "win-x64", Repo);

        Assert.Null(meta);
    }

    /// <summary>验证他仓/异段 href 被仓库防御拦下：即使文件名碰巧匹配也不取（防替换页/串仓拿他仓资产当更新装）。</summary>
    [Fact]
    public void Pick_SkipsForeignRepoOrNonDownloadSegment_EvenIfNameMatches()
    {
        string own = $"/{Repo}/releases/download/v0.1.21/app_0.1.21_linux-amd64.deb";
        // 他仓同名资产 + 本仓非 download 段（如 /releases/tag/...）——都必须被防御跳过
        string[] hrefs =
        [
            "/attacker/app/releases/download/v0.1.21/app_0.1.21_linux-amd64.deb",
            $"/{Repo}/releases/tag/app_0.1.21_linux-amd64.deb",
            own,
        ];

        var meta = ReleaseMeta.Pick("v0.1.21", hrefs, "linux-x64", Repo, UpdatePlatform.Deb);

        Assert.NotNull(meta);
        Assert.Equal("https://github.com" + own, meta.AssetUrl);
    }

    /// <summary>验证绝对 URL（https://github.com/...）输入不被接受——expanded_assets 页恒产相对路径，
    /// 绝对 URL 会绕过 StartsWith(repoSegment) 的仓库防御（其前缀是 https:// 而非 /owner/repo/），
    /// 按非本仓跳过返回 null；防替换页/串仓拿他仓资产当更新装。</summary>
    [Fact]
    public void Pick_AbsoluteHref_IsRejectedLikeForeignRepo()
    {
        string abs = $"https://github.com/{Repo}/releases/download/v0.1.21/app_0.1.21_linux-amd64.deb";
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            abs,
            $"https://github.com/{Repo}/releases/download/v0.1.21/SHA256SUMS.txt",
        ], "linux-x64", Repo, UpdatePlatform.Deb);

        Assert.Null(meta);
    }

    /// <summary>验证 sha 缺失时仍返回资产 meta 且 Sha256Url=null（拒装决策由调用方/下载器 fail loud 执行）。</summary>
    [Fact]
    public void Pick_MissingSha_ReturnsMetaWithNullShaUrl()
    {
        var meta = ReleaseMeta.Pick("v0.1.21",
        [
            $"/{Repo}/releases/download/v0.1.21/app_0.1.21_linux-amd64.deb",
        ], "linux-x64", Repo, UpdatePlatform.Deb);

        Assert.NotNull(meta);
        Assert.Equal("app_0.1.21_linux-amd64.deb", meta.AssetName);
        Assert.Null(meta.Sha256Url);
    }

    /// <summary>验证包类型检测优先级：有 dpkg 取 Deb、仅 rpm 取 Rpm、两者皆无回退 Deb（保证装不上时错误可诊断）。</summary>
    [Fact]
    public void DetectPackageKind_PrefersDpkg_FallsBackRpm()
    {
        Assert.Equal(UpdatePlatform.Deb, UpdatePlatform.DetectPackageKind(hasDpkg: true, hasRpm: false));
        Assert.Equal(UpdatePlatform.Deb, UpdatePlatform.DetectPackageKind(hasDpkg: true, hasRpm: true));
        Assert.Equal(UpdatePlatform.Rpm, UpdatePlatform.DetectPackageKind(hasDpkg: false, hasRpm: true));
        // 两者皆无回退 deb：装不上时错误信息仍可诊断
        Assert.Equal(UpdatePlatform.Deb, UpdatePlatform.DetectPackageKind(hasDpkg: false, hasRpm: false));
    }

    /// <summary>验证 SHA256SUMS 解析兼容「双空格」与「*」二进制两类行形态，缺席条目返回 null。</summary>
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

    /// <summary>验证下载锁跨实例互斥（第二实例拿不到锁，防双写 .part 损坏），释放后语义等价于进程死亡自动释放。</summary>
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
