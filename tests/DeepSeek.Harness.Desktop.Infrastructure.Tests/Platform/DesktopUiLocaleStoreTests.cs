namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>
/// UI 语言持久化文件实现契约（ADR ui-copy-bilingual-completion）：落 desktop profile 目录、
/// 空/缺失/损坏一律 null（上层回退 OS locale）。与 home 环境变量改写的测试同集合串行。
/// </summary>
[Collection("dsh-home-env")]
public class DesktopUiLocaleStoreTests
{
    /// <summary>验证 Save 后 Load 回读同一值，且文件落在当前 profile 目录（与端口/PID 记忆同族）。</summary>
    [Fact]
    public void Save_ThenLoad_RoundTrips_InProfileDir()
    {
        string home = TempDir("dsh-ui-locale-");
        SetHome(home);
        try
        {
            var store = new DesktopUiLocaleStore();
            store.Save("en");

            Assert.Equal("en", store.Load());
            Assert.True(File.Exists(Path.Combine(
                home, "profiles", HarnessRuntimeHost.DesktopProfileName, "ui-locale")));
        }
        finally
        {
            ClearHome();
            SafeDelete(home);
        }
    }

    /// <summary>验证无记录（文件不存在）返回 null 而非抛异常。</summary>
    [Fact]
    public void Load_NoRecord_ReturnsNull()
    {
        string home = TempDir("dsh-ui-locale-none-");
        SetHome(home);
        try
        {
            Assert.Null(new DesktopUiLocaleStore().Load());
        }
        finally
        {
            ClearHome();
            SafeDelete(home);
        }
    }

    /// <summary>验证空白内容视为无记录（读取即 trim），不让空白劫持语言判定。</summary>
    [Fact]
    public void Load_BlankContent_ReturnsNull()
    {
        string home = TempDir("dsh-ui-locale-blank-");
        SetHome(home);
        try
        {
            string dir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ui-locale"), "   \n");

            Assert.Null(new DesktopUiLocaleStore().Load());
        }
        finally
        {
            ClearHome();
            SafeDelete(home);
        }
    }

    /// <summary>验证记录位置被目录占用（非普通文件）时按无记录处理，不抛异常。</summary>
    [Fact]
    public void Load_PathOccupiedByDirectory_ReturnsNull()
    {
        string home = TempDir("dsh-ui-locale-dir-");
        SetHome(home);
        try
        {
            Directory.CreateDirectory(Path.Combine(
                home, "profiles", HarnessRuntimeHost.DesktopProfileName, "ui-locale"));

            Assert.Null(new DesktopUiLocaleStore().Load());
        }
        finally
        {
            ClearHome();
            SafeDelete(home);
        }
    }

    private static string TempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDelete(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void SetHome(string home)
    {
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, home);
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.EcosystemHomeEnv, null);
    }

    private static void ClearHome()
    {
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, null);
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.EcosystemHomeEnv, null);
    }
}
