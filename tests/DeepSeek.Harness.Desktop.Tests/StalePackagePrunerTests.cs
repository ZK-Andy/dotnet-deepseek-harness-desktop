using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 自更新安装包清扫的纯判据与目录行为回归（ADR self-update-prune-consumed-packages）：
/// 过期包删除、当前/待装保护、废弃残留清理、下载锁竞态保留、.part 半成品排除——
/// 清扫保守性（绝不误删可安装资产与在途下载物）。
/// </summary>
public class StalePackagePrunerTests
{
    // —— TryExtractVersion ——

    /// <summary>验证本应用 rpm/deb/exe/dmg 文件名能提取出版本段（前缀后至第一个 _）。</summary>
    [Theory]
    [InlineData("deepseek-harness-desktop-0.4.4_linux-x86_64.rpm", "0.4.4")]
    [InlineData("deepseek-harness-desktop-0.3.12_linux-amd64.deb", "0.3.12")]
    [InlineData("deepseek-harness-desktop-0.4.5_windows-x64-setup.exe", "0.4.5")]
    [InlineData("deepseek-harness-desktop-0.4.5_macos-arm64.dmg", "0.4.5")]
    public void TryExtractVersion_RecognizesAppAssets(string fileName, string expected)
    {
        Assert.Equal(expected, StalePackagePruner.TryExtractVersion(fileName));
    }

    /// <summary>验证非本应用文件名（他物/无前缀/缺 _ 分隔）返回 null，不参与清扫。</summary>
    [Theory]
    [InlineData("other-app-0.4.4_linux-x86_64.rpm")]
    [InlineData("deepseek-harness-desktop-0.4.4rpm")]
    [InlineData("deepseek-harness-desktop_linux-x86_64.rpm")]
    [InlineData("install.log")]
    [InlineData("ready.json")]
    public void TryExtractVersion_UnknownNames_ReturnNull(string fileName)
    {
        Assert.Null(StalePackagePruner.TryExtractVersion(fileName));
    }

    /// <summary>验证 .part 下载半成品（前缀 + 版本可解析形态）返回 null——半成品归下载器自清，清扫绝不碰。</summary>
    [Theory]
    [InlineData("deepseek-harness-desktop-0.4.4_linux-x86_64.rpm.part")]
    [InlineData("deepseek-harness-desktop-0.3.12_linux-amd64.deb.part")]
    public void TryExtractVersion_PartialDownload_ReturnNull(string fileName)
    {
        Assert.Null(StalePackagePruner.TryExtractVersion(fileName));
    }

    // —— SelectStale（纯判据）——

    private static IEnumerable<string> Assets(params string[] names) => names;

    /// <summary>验证严格旧于当前的包被选中，当前与更新的包保留。</summary>
    [Fact]
    public void SelectStale_DeletesOnlyOlderThanCurrent()
    {
        HashSet<string> stale = StalePackagePruner.SelectStale(
            Assets(
                "deepseek-harness-desktop-0.4.3_linux-x86_64.rpm",
                "deepseek-harness-desktop-0.4.4_linux-x86_64.rpm",
                "deepseek-harness-desktop-0.4.5_linux-x86_64.rpm"),
            currentVersion: "0.4.4");

        Assert.Equal(new HashSet<string> { "deepseek-harness-desktop-0.4.3_linux-x86_64.rpm" }, stale);
    }

    /// <summary>验证 .part 半成品（即使版本旧于当前）不被选中——在途下载物绝不删。</summary>
    [Fact]
    public void SelectStale_PartialDownload_NeverSelected()
    {
        HashSet<string> stale = StalePackagePruner.SelectStale(
            Assets(
                "deepseek-harness-desktop-0.4.4_linux-x86_64.rpm.part",
                "deepseek-harness-desktop-0.4.3_linux-x86_64.rpm"),
            currentVersion: "0.4.5");

        Assert.Equal(new HashSet<string> { "deepseek-harness-desktop-0.4.3_linux-x86_64.rpm" }, stale);
    }

    /// <summary>验证版本解析失败（脏文件）与未知文件被保守跳过，不因清扫引入误删。</summary>
    [Fact]
    public void SelectStale_Unparseable_AndUnknown_Skipped()
    {
        HashSet<string> stale = StalePackagePruner.SelectStale(
            Assets(
                "deepseek-harness-desktop-garbage_linux-x86_64.rpm",
                "deepseek-harness-desktop-0.4.4_linux-x86_64.rpm",
                "some-other-file.rpm"),
            currentVersion: "0.4.5");

        Assert.Equal(new HashSet<string> { "deepseek-harness-desktop-0.4.4_linux-x86_64.rpm" }, stale);
    }

    // —— Run（目录行为）——

    /// <summary>验证 Run 删除过期包与 install.sh 废弃残留，保留当前版本包（≥ 当前天然不在清扫范围）。</summary>
    [Fact]
    public void Run_DeletesStaleAndLegacy_KeepsCurrentAndNewer()
    {
        string dir = CreateTempUpdatesDir();
        try
        {
            string stale = Path.Combine(dir, "deepseek-harness-desktop-0.4.3_linux-x86_64.rpm");
            string current = Path.Combine(dir, "deepseek-harness-desktop-0.4.4_linux-x86_64.rpm");
            string newer = Path.Combine(dir, "deepseek-harness-desktop-0.4.5_linux-x86_64.rpm");
            string installSh = Path.Combine(dir, "install.sh");
            File.WriteAllBytes(stale, [1]);
            File.WriteAllBytes(current, [1]);
            File.WriteAllBytes(newer, [1]);
            File.WriteAllBytes(installSh, [1]);

            StalePackagePruner.Run(dir, currentVersion: "0.4.4");

            Assert.False(File.Exists(stale), "过期包应被删");
            Assert.True(File.Exists(current), "当前版本包应保留");
            Assert.True(File.Exists(newer), "更新版本包（潜在待装）应保留");
            Assert.False(File.Exists(installSh), "install.sh 废弃残留应被删");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证无持有者的 .download.lock 死锁文件被删，下次下载重建无碍。</summary>
    [Fact]
    public void Run_UnheldDownloadLock_IsDeleted()
    {
        string dir = CreateTempUpdatesDir();
        try
        {
            string lockFile = Path.Combine(dir, ".download.lock");
            File.WriteAllBytes(lockFile, []);

            StalePackagePruner.Run(dir, currentVersion: "0.4.4");

            Assert.False(File.Exists(lockFile), "无持有者的死锁文件应被删");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证下载锁被他实例持有（FileShare.None 打不开）时整个清扫跳过——过期包与死残留都不动，
    /// 防与在途下载竞态（ADR：对账不动 .part 与锁）。</summary>
    [Fact]
    public void Run_HeldDownloadLock_AbortsEntireSweep()
    {
        string dir = CreateTempUpdatesDir();
        string stale = Path.Combine(dir, "deepseek-harness-desktop-0.4.3_linux-x86_64.rpm");
        string installSh = Path.Combine(dir, "install.sh");
        string lockFile = Path.Combine(dir, ".download.lock");
        File.WriteAllBytes(stale, [1]);
        File.WriteAllBytes(installSh, [1]);
        FileStream? held = null;
        try
        {
            // 模拟他实例持有下载锁：FileShare.None 独占打开后保持句柄
            held = File.Open(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            StalePackagePruner.Run(dir, currentVersion: "0.4.4");

            // 持锁 = 他实例下载中：过期包与 install.sh 都不该删
            Assert.True(File.Exists(stale), "持锁期间过期包不得删除");
            Assert.True(File.Exists(installSh), "持锁期间废弃残留不得删除");
        }
        finally
        {
            // 先释放锁句柄才能清理目录（Windows 上持句柄删目录会抛）
            held?.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证目录不存在时 Run 静默返回（清扫是增强，绝不因此报错）。</summary>
    [Fact]
    public void Run_MissingDir_NoOp()
    {
        // 不存在的目录：无异常静默返回
        StalePackagePruner.Run(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"), currentVersion: "0.4.4");
    }

    private static string CreateTempUpdatesDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"updates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
