using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>事务化插件管线（ADR transactional-plugin-pipeline）的单元覆盖：staging 拷贝/换入/作废、
/// journal 各步 recover（重放/回滚/尾巴清理）、journal 损坏 fail loud、stray 残留清扫。
/// 中断注入 = 手工摆出 journal + 目录形态（等价于两步 rename 之间被 kill）。</summary>
public class PluginProfileTransactionTests
{
    private static string NewHome()
        => Path.Combine(Path.GetTempPath(), "tx-" + Guid.NewGuid().ToString("N"));

    private static string ProfileDir(string home)
        => Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);

    private static string SeedActiveProfile(string home, string marker)
    {
        Directory.CreateDirectory(ProfileDir(home));
        File.WriteAllText(Path.Combine(ProfileDir(home), "package.json"), $$"""{"marker":"{{marker}}"}""");
        // 运行时管理文件：staging 拷贝必须排除
        File.WriteAllText(Path.Combine(ProfileDir(home), ".dsh-web-port"), "40001");
        return home;
    }

    /// <summary>验证 Begin 整目录拷贝 active profile 到 staging，且排除端口/PID 等运行时管理文件。</summary>
    [Fact]
    public void Begin_CopiesProfile_ExcludingRuntimeManagedFiles()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            var tx = PluginProfileTransaction.Begin(home, _ => { });

            string stagedPkg = Path.Combine(tx.StagingProfileDir, "package.json");
            Assert.True(File.Exists(stagedPkg));
            Assert.Contains("old", File.ReadAllText(stagedPkg));
            // 端口/PID 等运行时管理文件不随拷贝
            Assert.False(File.Exists(Path.Combine(tx.StagingProfileDir, ".dsh-web-port")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 active profile 缺失时 Begin 抛异常（调用链保证 EnsureProfile 先行，缺序应炸出来）。</summary>
    [Fact]
    public void Begin_Throws_WhenActiveProfileMissing()
    {
        string home = NewHome();
        try
        {
            Assert.Throws<InvalidOperationException>(() => PluginProfileTransaction.Begin(home, _ => { }));
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证 Activate 两步 rename 换入后 active 为新态、journal/rollback/staging 全部收口。</summary>
    [Fact]
    public void Activate_SwapsStagingIn_AndCleansJournalAndRollback()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            var tx = PluginProfileTransaction.Begin(home, _ => { });
            File.WriteAllText(Path.Combine(tx.StagingProfileDir, "package.json"), """{"marker":"new"}""");

            tx.Activate();

            Assert.Contains("new", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(home, "profiles"), ".rollback-*"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 Discard 清除 staging home 且 active 保持旧态。</summary>
    [Fact]
    public void Discard_RemovesStaging_ActiveUntouched()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            var tx = PluginProfileTransaction.Begin(home, _ => { });
            tx.Discard();

            Assert.Contains("old", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal 在 Prepared 时 recover 作废 staging、active 原封不动。</summary>
    [Fact]
    public void Recover_Prepared_DiscardsStaging_KeepsActive()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            // 中断注入：Begin 后（journal Prepared）即被 kill——active 完整、staging 残留
            var tx = PluginProfileTransaction.Begin(home, _ => { });
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"),
                $$"""{"SchemaVersion":1,"StagingHome":"{{tx.StagingHome}}","StagingProfile":"{{tx.StagingProfileDir}}","RollbackDir":"x","Step":"Prepared"}""");

            PluginProfileTransaction.Recover(home, _ => { });

            Assert.Contains("old", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal 在 ActiveMoved 且 staging 在位时 recover 重放换入（forward）。</summary>
    [Fact]
    public void Recover_ActiveMoved_ReplaysActivationForward()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            // 中断注入：active→rollback 完成后、staging→active 之前被 kill
            string rollback = Path.Combine(home, "profiles", ".rollback-abc");
            Directory.Move(ProfileDir(home), rollback);
            string stagingHome = Path.Combine(home, ".tx-abc");
            Directory.CreateDirectory(Path.Combine(stagingHome, "profiles"));
            Directory.CreateDirectory(Path.Combine(stagingHome, "profiles", HarnessRuntimeHost.DesktopProfileName));
            File.WriteAllText(Path.Combine(stagingHome, "profiles", HarnessRuntimeHost.DesktopProfileName, "package.json"), """{"marker":"new"}""");
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"),
                $$"""{"SchemaVersion":1,"StagingHome":"{{stagingHome}}","StagingProfile":"{{Path.Combine(stagingHome, "profiles", HarnessRuntimeHost.DesktopProfileName)}}","RollbackDir":"{{rollback}}","Step":"ActiveMoved"}""");

            PluginProfileTransaction.Recover(home, _ => { });

            Assert.Contains("new", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(home, "profiles"), ".rollback-*"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal 在 ActiveMoved 但 staging 缺失时 recover 用 rollback 回滚恢复。</summary>
    [Fact]
    public void Recover_ActiveMoved_StagingMissing_RollsBack()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            // 中断注入：active 已让位 rollback，staging 被外力清掉 → 只能回滚恢复旧 profile
            string rollback = Path.Combine(home, "profiles", ".rollback-abc");
            Directory.Move(ProfileDir(home), rollback);
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"),
                $$"""{"SchemaVersion":1,"StagingHome":"{{Path.Combine(home, ".tx-abc")}}","StagingProfile":"{{Path.Combine(home, ".tx-abc", "profiles", HarnessRuntimeHost.DesktopProfileName)}}","RollbackDir":"{{rollback}}","Step":"ActiveMoved"}""");

            PluginProfileTransaction.Recover(home, _ => { });

            Assert.Contains("old", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal 在 StagingActivated 时 recover 只清尾巴（active 已是新态、回收 rollback）。</summary>
    [Fact]
    public void Recover_StagingActivated_CleansTailOnly()
    {
        string home = SeedActiveProfile(NewHome(), "new");
        try
        {
            // 中断注入：换入已完成、journal 删除前被 kill；rollback 尚未回收
            string rollback = Path.Combine(home, "profiles", ".rollback-abc");
            Directory.CreateDirectory(rollback);
            File.WriteAllText(Path.Combine(rollback, "package.json"), """{"marker":"old"}""");
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"),
                $$"""{"SchemaVersion":1,"StagingHome":"{{Path.Combine(home, ".tx-abc")}}","StagingProfile":"x","RollbackDir":"{{rollback}}","Step":"StagingActivated"}""");

            PluginProfileTransaction.Recover(home, _ => { });

            Assert.Contains("new", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(home, "profiles"), ".rollback-*"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal 损坏时 recover fail loud 抛异常，不静默清理。</summary>
    [Fact]
    public void Recover_CorruptJournal_FailsLoud()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"), "{not json");
            Assert.Throws<InvalidOperationException>(() => PluginProfileTransaction.Recover(home, _ => { }));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal schema 版本不认识时 recover fail loud。</summary>
    [Fact]
    public void Recover_UnknownSchemaVersion_FailsLoud()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"),
                """{"SchemaVersion":99,"StagingHome":"x","StagingProfile":"x","RollbackDir":"x","Step":"Prepared"}""");
            Assert.Throws<InvalidOperationException>(() => PluginProfileTransaction.Recover(home, _ => { }));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证无 journal 时 recover 清扫 stray .tx-*/.rollback-* 残留目录。</summary>
    [Fact]
    public void Recover_NoPending_CleansStrayDirs()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            // 崩溃残留：journal 已不在（或从未写出），staging/rollback 目录成无主
            Directory.CreateDirectory(Path.Combine(home, ".tx-dead", "profiles"));
            Directory.CreateDirectory(Path.Combine(home, "profiles", ".rollback-dead"));

            PluginProfileTransaction.Recover(home, _ => { });

            Assert.Empty(Directory.GetDirectories(home, ".tx-*"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(home, "profiles"), ".rollback-*"));
            Assert.Contains("old", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal 在 Prepared 但 rollback 在位（rename 后断 journal 的崩溃窗口）时 recover 回滚恢复 active。</summary>
    [Fact]
    public void Recover_Prepared_RollbackPresent_RestoresActive()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            string rollback = Path.Combine(home, "profiles", ".rollback-abc");
            Directory.Move(ProfileDir(home), rollback);
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"),
                $$"""{"SchemaVersion":1,"StagingHome":"{{Path.Combine(home, ".tx-abc")}}","StagingProfile":"x","RollbackDir":"{{rollback}}","Step":"Prepared"}""");

            PluginProfileTransaction.Recover(home, _ => { });

            Assert.Contains("old", File.ReadAllText(Path.Combine(ProfileDir(home), "package.json")));
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证 journal 在 ActiveMoved 但 staging 与 rollback 均缺失（active 缺位最险态）时 recover fail loud。</summary>
    [Fact]
    public void Recover_ActiveMoved_BothMissing_FailsLoud()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            Directory.Delete(ProfileDir(home), recursive: true);
            File.WriteAllText(Path.Combine(home, "profiles", ".pending.json"),
                $$"""{"SchemaVersion":1,"StagingHome":"{{Path.Combine(home, ".tx-abc")}}","StagingProfile":"{{Path.Combine(home, ".tx-abc", "profiles", HarnessRuntimeHost.DesktopProfileName)}}","RollbackDir":"{{Path.Combine(home, "profiles", ".rollback-abc")}}","Step":"ActiveMoved"}""");

            Assert.Throws<InvalidOperationException>(() => PluginProfileTransaction.Recover(home, _ => { }));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
