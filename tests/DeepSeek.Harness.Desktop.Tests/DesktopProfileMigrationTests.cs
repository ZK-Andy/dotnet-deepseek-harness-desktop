using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 桌面 profile 改名迁移回归（ADR desktop-profile-rename）：旧名 <c>desktop</c> 目录在新名
/// spawn 前一次性改名——内容整体保留、新目录已存在时不合并、旧目录归官方 Electron 端时让路
/// （绝不搬官方数据）、无旧目录零操作、目标位被占时日志留痕且不阻断启动。
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

    /// <summary>验证官方端标识文件任一存在即判定归官方所有。</summary>
    /// <param name="marker">标识文件名。</param>
    [Theory]
    [InlineData("desktop.cordis.yml")]
    [InlineData("desktop-packages.json")]
    [InlineData("desktop-release.json")]
    public void IsOfficialDesktopProject_TrueForMarkerFiles(string marker)
    {
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, marker), "x");
            Assert.True(DesktopProfileBootstrap.IsOfficialDesktopProject(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证已装官方 host 依赖（无标识文件）同样判定归官方所有。</summary>
    [Fact]
    public void IsOfficialDesktopProject_TrueForHostDependency()
    {
        string dir = NewDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh-desktop-host"));
            Assert.True(DesktopProfileBootstrap.IsOfficialDesktopProject(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证官方清单名（<c>package.json</c> 的 name）单独存在也判定归官方所有——覆盖
    /// 事务中断/残留目录（标识文件尚未落盘的窄窗口）。</summary>
    [Fact]
    public void IsOfficialDesktopProject_TrueForOfficialManifestName()
    {
        string dir = NewDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "package.json"),
                $$"""{"name":"{{DesktopProfileBootstrap.OfficialProjectManifestName}}"}""");
            Assert.True(DesktopProfileBootstrap.IsOfficialDesktopProject(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证清单不可读（非法 JSON）时判否：无法证明官方归属即按存量迁移，不因清单异常放弃迁移。</summary>
    [Fact]
    public void IsOfficialDesktopProject_FalseForUnreadableManifest()
    {
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{ not json");
            Assert.False(DesktopProfileBootstrap.IsOfficialDesktopProject(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证我方形态（package.json + cordis.patch.yml + 普通插件依赖）与空目录都不被误判为官方项目——
    /// 判别只用于「不搬官方数据」一侧，识别不出官方特征即按存量迁移。</summary>
    [Fact]
    public void IsOfficialDesktopProject_FalseForOurShapeAndEmpty()
    {
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), """{"name":"dsh-profile-desktop"}""");
            File.WriteAllText(Path.Combine(dir, "cordis.patch.yml"), "[]");
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "dshmarket"));

            Assert.False(DesktopProfileBootstrap.IsOfficialDesktopProject(dir));
            Assert.False(DesktopProfileBootstrap.IsOfficialDesktopProject(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证旧目录归官方端所有时迁移让路：官方数据原地不动、不新建 dotnet-desktop、日志留痕。</summary>
    [Fact]
    public void OfficialProject_MigrationSkipped_LegacyKept()
    {
        string home = NewDir();
        try
        {
            string legacyDir = Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName);
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "desktop.cordis.yml"), "[]");
            File.WriteAllText(Path.Combine(legacyDir, "desktop-release.json"), "{}");

            var logs = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, logs.Add);

            Assert.True(Directory.Exists(legacyDir));
            Assert.True(File.Exists(Path.Combine(legacyDir, "desktop-release.json")));
            Assert.False(Directory.Exists(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName)));
            Assert.Contains(logs, m => m.Contains("归官方 Electron 桌面端所有"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证官方 host 依赖存在（无标识文件）同样让路，不搬其 node_modules。</summary>
    [Fact]
    public void OfficialHostDependency_MigrationSkipped()
    {
        string home = NewDir();
        try
        {
            string legacyDir = Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName);
            Directory.CreateDirectory(Path.Combine(legacyDir, "node_modules", "@deepseek-ai", "dsh-desktop-host"));

            var logs = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, logs.Add);

            Assert.True(Directory.Exists(Path.Combine(legacyDir, "node_modules", "@deepseek-ai", "dsh-desktop-host")));
            Assert.False(Directory.Exists(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName)));
            Assert.Contains(logs, m => m.Contains("归官方 Electron 桌面端所有"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证我方形态的存量目录端到端仍被迁移：清单/cordis.patch.yml/插件/端口记忆整体保留、旧目录消失。</summary>
    [Fact]
    public void OurShapeLegacyDir_StillMigrates_EndToEnd()
    {
        string home = NewDir();
        try
        {
            string legacyDir = Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName);
            Directory.CreateDirectory(Path.Combine(legacyDir, "node_modules", "dshmarket"));
            File.WriteAllText(Path.Combine(legacyDir, "package.json"), """{"name":"dsh-profile-desktop"}""");
            File.WriteAllText(Path.Combine(legacyDir, "cordis.patch.yml"), "[]");
            File.WriteAllText(Path.Combine(legacyDir, ".dsh-web-port"), "4242");

            var logs = new List<string>();
            DesktopProfileBootstrap.MigrateLegacyProfileName(home, logs.Add);

            string newDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            Assert.False(Directory.Exists(legacyDir));
            Assert.True(File.Exists(Path.Combine(newDir, "package.json")));
            Assert.True(File.Exists(Path.Combine(newDir, "cordis.patch.yml")));
            Assert.True(Directory.Exists(Path.Combine(newDir, "node_modules", "dshmarket")));
            Assert.Equal("4242", File.ReadAllText(Path.Combine(newDir, ".dsh-web-port")));
            Assert.Contains(logs, m => m.Contains("已迁移"));
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
