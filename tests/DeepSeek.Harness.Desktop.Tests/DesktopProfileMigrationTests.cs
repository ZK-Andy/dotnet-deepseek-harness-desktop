using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 桌面 profile 改名迁移回归（ADR desktop-profile-rename）：旧名 <c>desktop</c> 目录在新名
/// spawn 前一次性改名——内容整体保留、新目录已存在时不合并、无旧目录零操作、
/// 目标位被占时日志留痕且不阻断启动。
/// </summary>
public class DesktopProfileMigrationTests
{
    /// <summary>验证旧名目录存在时被整体改名：文件内容原样保留，旧目录消失。</summary>
    [Fact]
    public void LegacyDirPresent_Renamed_ContentPreserved()
    {
        string home = NewDir();
        try
        {
            string legacyPort = Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName, ".dsh-web-port");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPort)!);
            File.WriteAllText(legacyPort, "4242");
            Directory.CreateDirectory(Path.Combine(
                home, "profiles", DesktopProfileBootstrap.LegacyProfileName, "node_modules", "some-plugin"));

            var logs = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, logs.Add);

            string newDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            Assert.True(Directory.Exists(newDir));
            Assert.Equal("4242", File.ReadAllText(Path.Combine(newDir, ".dsh-web-port")));
            Assert.True(Directory.Exists(Path.Combine(newDir, "node_modules", "some-plugin")));
            Assert.False(Directory.Exists(Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName)));
            Assert.Contains(logs, m => m.Contains("已迁移"));

            // 幂等：旧目录已消失，重复调用走「旧目录不存在」早退，零日志零动作
            var second = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, second.Add);
            Assert.Empty(second);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证新目录已存在时跳过迁移：不合并两目录、不动新目录内容、旧目录原样保留。</summary>
    [Fact]
    public void NewDirExists_SkipMerge_BothDirsUntouched()
    {
        string home = NewDir();
        try
        {
            string legacyMarker = Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName, "legacy.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyMarker)!);
            File.WriteAllText(legacyMarker, "legacy");
            string newMarker = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "current.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(newMarker)!);
            File.WriteAllText(newMarker, "current");

            var logs = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, logs.Add);

            Assert.Equal("current", File.ReadAllText(newMarker));
            Assert.False(File.Exists(Path.Combine(
                home, "profiles", HarnessRuntimeHost.DesktopProfileName, "legacy.txt")));
            Assert.Equal("legacy", File.ReadAllText(legacyMarker));
            Assert.Contains(logs, m => m.Contains("跳过"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证无旧目录时零操作：不抛异常、也不预创建新目录（自举是 EnsureProfile 的职责）。</summary>
    [Fact]
    public void NoLegacyDir_NoOp_DoesNotCreateNewDir()
    {
        string home = NewDir();
        try
        {
            var logs = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, logs.Add);

            // 零操作：不预创建 profiles/（自举是 EnsureProfile 的职责）
            Assert.False(Directory.Exists(Path.Combine(home, "profiles")));
            Assert.Empty(logs);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证目标位被占（同名常规文件）时迁移失败不抛出：旧目录原样保留、日志给足信号、
    /// 不阻断启动（后续由 EnsureProfile 全新自举兜底）。</summary>
    [Fact]
    public void TargetOccupiedByFile_FailureLogged_NoThrow_LegacyKept()
    {
        string home = NewDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName));
            // 目标位放一个常规文件：Directory.Exists 为 false 但 Move 必败——确定性的跨平台失败注入
            File.WriteAllText(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName), "occupied");

            var logs = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, logs.Add);

            Assert.True(Directory.Exists(Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName)));
            Assert.False(Directory.Exists(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName)));
            Assert.Contains(logs, m => m.Contains("迁移失败") && m.Contains("保留"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
