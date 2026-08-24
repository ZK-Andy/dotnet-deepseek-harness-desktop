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

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
