using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>desktop profile 自举（对齐上游 initProfile 语义）：三件套创建、幂等、永不覆写已有文件。</summary>
public class DesktopProfileBootstrapTests
{
    [Fact]
    public void EnsureProfile_FreshHome_CreatesManifestWithWebAppBundles_AndSiblingFiles()
    {
        var home = NewDir();
        try
        {
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));

            var dir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            var manifest = File.ReadAllText(Path.Combine(dir, "package.json"));
            // bundles 必须含 web-app——缺它 `dsh web:` 永远出不来（拒启教训）
            Assert.Contains("\"@deepseek-ai/dsh-base\"", manifest);
            Assert.Contains("\"@deepseek-ai/dsh-web-app\"", manifest);
            Assert.Contains("\"private\": true", manifest);
            Assert.Equal(DesktopProfileBootstrap.PatchTemplate, File.ReadAllText(Path.Combine(dir, "cordis.patch.yml")));
            Assert.Equal(DesktopProfileBootstrap.PnpmWorkspaceTemplate, File.ReadAllText(Path.Combine(dir, "pnpm-workspace.yaml")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void EnsureProfile_AlreadyInitialized_NeverTouchesExistingFiles()
    {
        var home = NewDir();
        try
        {
            var dir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            Directory.CreateDirectory(dir);
            // 用户/dsh 已管理的清单：bundles 是用户状态，自举绝不能改写（版本感知升级走 plugin add 管线）
            const string userManifest = """{ "name": "mine", "dsh": { "profile": { "bundles": ["my-plugin"] } } }""";
            File.WriteAllText(Path.Combine(dir, "package.json"), userManifest);
            const string userPatch = "# mine\n";
            File.WriteAllText(Path.Combine(dir, "cordis.patch.yml"), userPatch);

            Assert.False(DesktopProfileBootstrap.EnsureProfile(home));

            Assert.Equal(userManifest, File.ReadAllText(Path.Combine(dir, "package.json")));
            Assert.Equal(userPatch, File.ReadAllText(Path.Combine(dir, "cordis.patch.yml")));
            // 缺失的兄弟文件仍补齐（与上游 initProfile 的逐文件 existsSync 守卫一致）
            Assert.True(File.Exists(Path.Combine(dir, "pnpm-workspace.yaml")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ReconcileProfile_RemovesDeadLocalSeedRefs_KeepsRegistryAndPresentLocal()
    {
        var home = NewDir();
        try
        {
            var dir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "package.json");
            // dshmarket 为死掉的 file: 种子（退役后路径不存在）；companion 为 registry 形态（保留）；
            // alive 为仍存在的本地目录（保留）。bundles 同理对齐。
            var aliveDir = Path.Combine(dir, "alive-pkg");
            Directory.CreateDirectory(aliveDir);
            File.WriteAllText(Path.Combine(aliveDir, "package.json"), "{}");
            File.WriteAllText(manifestPath,
                "{\n  \"dependencies\": {\n" +
                "    \"dshmarket\": \"file:/gone/dshmarket.tgz\",\n" +
                "    \"dsh-desktop-companion\": \"^1.0.0\",\n" +
                "    \"alive\": \"" + aliveDir.Replace("\\", "\\\\") + "\"\n" +
                "  },\n  \"dsh\": {\"profile\": {\"bundles\": [\"dshmarket\", \"dsh-desktop-companion\", \"alive\"]}}\n}");

            var logs = new List<string>();
            var removed = DesktopProfileBootstrap.ReconcileProfile(home, logs.Add);

            Assert.Equal(1, removed);
            var after = File.ReadAllText(manifestPath);
            Assert.DoesNotContain("\"dshmarket\"", after);
            Assert.DoesNotContain("dshmarket", after);
            Assert.Contains("\"dsh-desktop-companion\"", after);
            Assert.Contains("\"alive\"", after);
            Assert.Contains(logs, l => l.Contains("dshmarket") && l.Contains("不可解析"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ReconcileProfile_NoOp_WhenNoDeadLocalRefs()
    {
        var home = NewDir();
        try
        {
            var dir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            Directory.CreateDirectory(dir);
            var manifestPath = Path.Combine(dir, "package.json");
            // 全部为 registry 形态或现存本地目标：无可移除项。
            var aliveDir = Path.Combine(dir, "alive-pkg");
            Directory.CreateDirectory(aliveDir);
            File.WriteAllText(Path.Combine(aliveDir, "package.json"), "{}");
            File.WriteAllText(manifestPath,
                "{\n  \"dependencies\": {\"a\": \"^1.0.0\", \"b\": \"" + aliveDir.Replace("\\", "\\\\") + "\"},\n  \"dsh\": {\"profile\": {\"bundles\": [\"a\", \"b\"]}}\n}");

            var logs = new List<string>();
            var removed = DesktopProfileBootstrap.ReconcileProfile(home, logs.Add);

            Assert.Equal(0, removed);
            Assert.Empty(logs);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ReconcileProfile_NoOp_WhenManifestMissing()
    {
        var home = NewDir();
        try
        {
            var logs = new List<string>();
            Assert.Equal(0, DesktopProfileBootstrap.ReconcileProfile(home, logs.Add));
            Assert.Empty(logs);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
