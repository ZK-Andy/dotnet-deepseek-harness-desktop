namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>桌面 profile 生命周期编排（migrate → recover → ensure → reconcile 四步）的端到端判定。</summary>
[Collection("dsh-home-env")]
public class ProfileLifecycleTests
{
    /// <summary>验证冷启动即自举出桌面 profile 三件套（清单 / cordis 补丁 / pnpm workspace），且二次调用幂等不覆写清单。</summary>
    [Fact]
    public void EnsureReady_ColdHome_BootstrapsProfile_AndIsIdempotent()
    {
        string home = NewDir();
        string? original = SwapHome(home);
        try
        {
            new ProfileLifecycle().EnsureReady();

            string profile = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            string manifest = Path.Combine(profile, "package.json");
            Assert.True(File.Exists(manifest));
            Assert.True(File.Exists(Path.Combine(profile, "cordis.patch.yml")));
            Assert.True(File.Exists(Path.Combine(profile, "pnpm-workspace.yaml")));
            Assert.Contains("dsh-web-app", File.ReadAllText(manifest));

            // 幂等：已自举的 profile 归 dsh/用户所有，重复调用零覆写。
            // 断言写哨兵而非自比较——清单内容是常量拼装，二次覆写也会逐字相同，自比较恒真。
            File.WriteAllText(manifest, "{\"sentinel\":true}");
            new ProfileLifecycle().EnsureReady();
            Assert.Contains("sentinel", File.ReadAllText(manifest), StringComparison.Ordinal);
        }
        finally
        {
            SwapHome(original);
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证旧名 profile 目录被迁移到新名：内容整体保留、旧目录消失、不与新目录合并。</summary>
    [Fact]
    public void EnsureReady_LegacyProfileDir_MigratesToDesktopProfileName()
    {
        string home = NewDir();
        string? original = SwapHome(home);
        try
        {
            string legacy = Path.Combine(home, "profiles", DesktopProfileBootstrap.LegacyProfileName);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "keep.txt"), "keep");

            new ProfileLifecycle().EnsureReady();

            string profile = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            Assert.False(Directory.Exists(legacy));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(profile, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(profile, "package.json")));
        }
        finally
        {
            SwapHome(original);
            Directory.Delete(home, recursive: true);
        }
    }

    private static string? SwapHome(string? value)
    {
        string? previous = Environment.GetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv);
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, value);
        return previous;
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-profilelife-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
