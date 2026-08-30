using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 共享 home 契约（ADR shared-home-desktop-profile）：home 解析优先级、上游规范默认值，
/// 以及「日志/updates 居 home 根、端口记忆按 profile 隔离」的布局契约防回归。
/// 与 HarnessRuntimeHostTests 同集合串行（两者都改写进程级 DSH_HOME 覆盖变量）。
/// </summary>
[Collection("dsh-home-env")]
public class SharedHomeContractTests
{
    /// <summary>验证桌面覆盖变量优先于生态 DSH_HOME 与默认 ~/.dsh 的 home 解析优先级。</summary>
    [Fact]
    public void ResolveDshHome_DesktopOverride_WinsOverEcosystemAndDefault()
    {
        string desktop = TempDir("dsh-contract-desktop-");
        string ecosystem = TempDir("dsh-contract-eco-");
        SetEnv(desktop, ecosystem);
        try
        {
            Assert.Equal(Path.GetFullPath(desktop), HarnessRuntimeHost.ResolveDshHome());
        }
        finally
        {
            ClearEnv();
            SafeDelete(desktop);
            SafeDelete(ecosystem);
        }
    }

    /// <summary>验证无桌面覆盖时生态标准 DSH_HOME 生效，且 ~ 前缀展开到用户主目录。</summary>
    [Fact]
    public void ResolveDshHome_EcosystemOverride_Honored_AndTildeExpanded()
    {
        // 无桌面覆盖：生态标准 DSH_HOME 生效（与 CLI/TUI/Web 同语义），~ 前缀展开到用户主目录
        SetEnv(null, "~/ecosystem-dsh-home");
        try
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "ecosystem-dsh-home");
            Assert.Equal(expected, HarnessRuntimeHost.ResolveDshHome());
        }
        finally
        {
            ClearEnv();
        }
    }

    /// <summary>验证空白 DSH_HOME 按上游语义视为未设，回退到规范默认 ~/.dsh 而非当前工作目录。</summary>
    [Fact]
    public void ResolveDshHome_WhitespaceEcosystem_TreatedAsUnset_FallsBackToCanonicalDotDsh()
    {
        // 上游语义：空白 DSH_HOME 视为未设（绝不落到 cwd）；默认 = 上游规范 home ~/.dsh
        SetEnv(null, "   ");
        try
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                HarnessRuntimeHost.DefaultHomeDirName);
            Assert.Equal(expected, HarnessRuntimeHost.ResolveDshHome());
        }
        finally
        {
            ClearEnv();
        }
    }

    /// <summary>验证端口记忆写入 profiles/desktop 下按 profile 隔离，旧全局位置仅保留作迁移回读不再写入。</summary>
    [Fact]
    public void PortMemory_LivesUnderDesktopProfile_LegacyPathKeptForMigrationRead()
    {
        // 布局契约（v0.3.5 实机事故修正）：端口记忆按 profile 隔离——桌面端与 web 会话
        // 共享 home，home 根的全局记忆曾让两类实例互抢端口（恢复屏循环直至重启电脑）。
        // 旧位置保留仅作迁移回读路径，不再写入。
        string home = TempDir("dsh-contract-root-");
        SetEnv(home, null);
        try
        {
            Assert.Equal(
                Path.Combine(home, "profiles", "desktop", ".dsh-web-port"),
                HarnessRuntimeHost.ResolvePortFilePath());
            Assert.Equal(
                Path.Combine(home, ".dsh-web-port"),
                HarnessRuntimeHost.ResolveLegacyPortFilePath());
        }
        finally
        {
            ClearEnv();
            SafeDelete(home);
        }
    }

    /// <summary>验证端到端装配契约：desktop profile 落 home 层、sessions 不落 profile 内、storages 居 home 层（由 DSH_TEST_E2E 门控）。</summary>
    [Fact]
    public async Task StartAsync_DesktopProfile_AssemblesAtHomeLevel_SessionsSharedAcrossProfiles_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        string home = TempDir("dsh-contract-e2e-");
        SetEnv(home, null);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");
        try
        {
            // 契约链路 = 壳的启动顺序：先自举 desktop profile（上游对自定义名不自动初始化，缺清单拒启），
            // 再以 --profile desktop 启动成功
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));

            using var host = new HarnessRuntimeHost();
            try
            {
                await host.StartAsync(TimeSpan.FromSeconds(30));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // PATH 没有 dsh——跳过
            }

            host.Stop();

            // 实证布局（rc.1/rc.2）：profile 装配落 <home>/profiles/desktop；会话/凭据居 home 层
            // （凭据 = <home>/.credentials.yaml，上游 credentials-local 源码钉死；sessions 懒建，
            // 但绝不落 profile 目录内）。storages/ 是启动即建的 home 层状态。
            Assert.True(
                Directory.Exists(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName)),
                $"desktop profile 应已装配：{home}/profiles/desktop");
            Assert.False(Directory.Exists(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName, "sessions")));
            Assert.True(Directory.Exists(Path.Combine(home, "storages")), $"storages 应在 home 层：{home}/storages");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            ClearEnv();
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
                // dsh 子进程尚未完全退出时句柄未释放：临时目录留给系统清理，不影响判定
            }
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

    private static void SetEnv(string? desktopOverride, string? ecosystem)
    {
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, desktopOverride);
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.EcosystemHomeEnv, ecosystem);
    }

    private static void ClearEnv() => SetEnv(null, null);
}
