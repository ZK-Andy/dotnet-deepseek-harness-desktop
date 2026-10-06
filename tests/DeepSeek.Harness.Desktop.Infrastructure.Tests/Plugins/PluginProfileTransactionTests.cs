
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Plugins;

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

    /// <summary>验证 staging 拷贝对符号链接重建链接本体而非穿链复制：有效文件/目录链接与悬空链接
    /// 都原样保留（悬空链曾令 File.Copy 抛 ENOENT、整个升级事务对 stale 链接的 profile 永久卡死
    /// ——2026-10-05 实机 .bin/node-which，ADR transactional-plugin-copy-preserves-symlinks）。</summary>
    [Fact]
    public void Begin_PreservesSymlinks_IncludingDangling()
    {
        string home = SeedActiveProfile(NewHome(), "old");
        try
        {
            // 摆出 node_modules 形态：有效文件链 + 目录链 + 悬空链（包已删、.bin 链接残留）
            string nm = Path.Combine(ProfileDir(home), "node_modules");
            string bin = Path.Combine(nm, ".bin");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(Path.Combine(nm, "real-pkg"));
            File.WriteAllText(Path.Combine(nm, "real-pkg", "index.js"), "x");
            File.CreateSymbolicLink(Path.Combine(bin, "valid-file"), Path.Combine("..", "real-pkg", "index.js"));
            Directory.CreateSymbolicLink(Path.Combine(nm, "linked-pkg"), Path.Combine("real-pkg"));
            File.CreateSymbolicLink(Path.Combine(bin, "node-which"), Path.Combine("..", "which", "bin", "node-which"));

            var logs = new List<string>();
            var tx = PluginProfileTransaction.Begin(home, logs.Add);

            string stagedBin = Path.Combine(tx.StagingProfileDir, "node_modules", ".bin");
            string stagedNm = Path.Combine(tx.StagingProfileDir, "node_modules");
            // 悬空链原样重建（不抛、不跟随）
            var dangling = new FileInfo(Path.Combine(stagedBin, "node-which"));
            Assert.Equal(Path.Combine("..", "which", "bin", "node-which"), dangling.LinkTarget);
            // 有效文件链重建且可解引用
            Assert.True(File.Exists(Path.Combine(stagedBin, "valid-file")));
            // 目录链重建后穿链可达内容
            Assert.True(File.Exists(Path.Combine(stagedNm, "linked-pkg", "index.js")));
            // 真实文件照常拷贝
            Assert.True(File.Exists(Path.Combine(stagedNm, "real-pkg", "index.js")));
            Assert.Contains(logs, l => l.Contains("悬空链接按文件链重建"));
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

    /// <summary>验证 active profile 为符号链接时 Begin 拒绝（fail loud），不穿链把链接目标当 profile 拷贝。</summary>
    [Fact]
    public void Begin_SymlinkedActiveProfile_Throws()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 符号链接行为按 Linux 断言
        }

        string home = NewHome();
        string outside = OutsideDir("tx-profile");
        try
        {
            File.WriteAllText(Path.Combine(outside, "package.json"), """{"marker":"outside"}""");
            Directory.CreateDirectory(Path.Combine(home, "profiles"));
            Directory.CreateSymbolicLink(ProfileDir(home), outside);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginProfileTransaction.Begin(home, _ => { }));

            Assert.Contains("符号链接", ex.Message);
        }
        finally
        {
            UnlinkThenDeleteHome(home, ProfileDir(home));
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>验证提交前 active profile 被换成符号链接时 Activate 拒绝，且拒链先于 journal 写出（不污染事务状态）。</summary>
    [Fact]
    public void Activate_SymlinkedActiveProfile_ThrowsBeforeWritingJournal()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 符号链接行为按 Linux 断言
        }

        string home = SeedActiveProfile(NewHome(), "old");
        string outside = OutsideDir("tx-activate");
        try
        {
            var tx = PluginProfileTransaction.Begin(home, _ => { });

            Directory.Delete(ProfileDir(home), recursive: true);
            Directory.CreateSymbolicLink(ProfileDir(home), outside);

            Assert.Throws<InvalidOperationException>(() => tx.Activate());
            Assert.False(File.Exists(Path.Combine(home, "profiles", ".pending.json")),
                "拒链必须先于 journal 写出");
        }
        finally
        {
            UnlinkThenDeleteHome(home, ProfileDir(home));
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>验证 pending journal 为符号链接时 Recover 拒绝，且链接目标原封不动。</summary>
    [Fact]
    public void Recover_SymlinkedJournal_Throws_TargetIntact()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 符号链接行为按 Linux 断言
        }

        string home = SeedActiveProfile(NewHome(), "old");
        string outside = Path.Combine(Path.GetTempPath(), $"tx-journal-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(outside, """{"SchemaVersion":1}""");
            File.CreateSymbolicLink(Path.Combine(home, "profiles", ".pending.json"), outside);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginProfileTransaction.Recover(home, _ => { }));

            Assert.Contains("符号链接", ex.Message);
            Assert.Equal("""{"SchemaVersion":1}""", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(Path.Combine(home, "profiles", ".pending.json"));
            Directory.Delete(home, recursive: true);
            File.Delete(outside);
        }
    }

    /// <summary>验证 stray staging 目录为符号链接时清扫跳过并记日志，链接目标内容不被递归删除。</summary>
    [Fact]
    public void Recover_StrayStagingSymlink_SkipsAndLogs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 符号链接行为按 Linux 断言
        }

        string home = SeedActiveProfile(NewHome(), "old");
        string outside = OutsideDir("tx-stray");
        var logs = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            Directory.CreateSymbolicLink(Path.Combine(home, ".tx-stray"), outside);

            PluginProfileTransaction.Recover(home, logs.Add);

            Assert.Contains(logs, l => l.Contains("符号链接"));
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")), "链接目标内容不得被删");
        }
        finally
        {
            Directory.Delete(Path.Combine(home, ".tx-stray"));
            Directory.Delete(home, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static string OutsideDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>验证目录链接建链无权限的降级语义：穿链递归复制整棵内容、本条计 1 降级
    /// （直测 internal 缝——真实触发面 Windows 缺 SeCreateSymbolicLinkPrivilege 无法在 POSIX CI 注入，
    /// ADR config-load-fail-safe-and-symlink-privilege-fallback）。</summary>
    [Fact]
    public void DegradeDirLink_CopiesTreeThroughLink()
    {
        string dir = OutsideDir("degrade-dir");
        string target = Path.Combine(dir, "staged-link");
        try
        {
            string source = Path.Combine(dir, "real-pkg");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "index.js"), "x");
            File.WriteAllText(Path.Combine(source, "sub", "deep.txt"), "y");
            string link = Path.Combine(dir, "linked-pkg");
            Directory.CreateSymbolicLink(link, Path.Combine("real-pkg"));

            var logs = new List<string>();
            int degraded = PluginStagingCopier.DegradeDirLink(link, target, logs.Add);

            Assert.Equal(1, degraded);
            Assert.Equal("x", File.ReadAllText(Path.Combine(target, "index.js")));
            Assert.Equal("y", File.ReadAllText(Path.Combine(target, "sub", "deep.txt")));
            Assert.Contains(logs, l => l.Contains("目录链接建链无权限"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证文件链接建链无权限的降级语义：有效目标穿链复制内容（File.Copy 跟随源链接）。</summary>
    [Fact]
    public void DegradeFileLink_ValidTarget_CopiesThroughLink()
    {
        string dir = OutsideDir("degrade-file");
        try
        {
            string realFile = Path.Combine(dir, "index.js");
            File.WriteAllText(realFile, "x");
            string link = Path.Combine(dir, "shim");
            File.CreateSymbolicLink(link, Path.Combine("index.js"));
            string target = Path.Combine(dir, "staged-shim");

            var logs = new List<string>();
            int degraded = PluginStagingCopier.DegradeFileLink(link, target, Path.Combine("index.js"), logs.Add);

            Assert.Equal(1, degraded);
            Assert.Equal("x", File.ReadAllText(target));
            Assert.Contains(logs, l => l.Contains("文件链接建链无权限"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证悬空文件链建链无权限时跳过并留痕：File.Copy 的 ENOENT 吞为跳过（含目标本身是
    /// 悬空链、File.Exists lstat 语义探不出的形态），绝不拖垮整个拷贝、不留半拷贝。</summary>
    [Fact]
    public void DegradeFileLink_DanglingTarget_SkipsAndLogs()
    {
        string dir = OutsideDir("degrade-dangling");
        try
        {
            string link = Path.Combine(dir, "node-which");
            File.CreateSymbolicLink(link, Path.Combine("which", "bin", "node-which"));
            string target = Path.Combine(dir, "staged-node-which");

            var logs = new List<string>();
            int degraded = PluginStagingCopier.DegradeFileLink(
                link, target, Path.Combine("which", "bin", "node-which"), logs.Add);

            Assert.Equal(1, degraded);
            Assert.False(File.Exists(target));
            Assert.Contains(logs, l => l.Contains("悬空链接建链无权限"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证降级路径的环守卫：已解析目标目录已在 visited 集合即跳过并留痕（pnpm 依赖互指的
    /// 目录链接图在无权限环境会无界递归栈溢出——2026-10-07 R2 评审，ADR config-load-fail-safe-and-symlink-privilege-fallback）。</summary>
    [Fact]
    public void DegradeDirLink_CycleVisited_SkipsAndLogs()
    {
        string dir = OutsideDir("degrade-cycle");
        string target = Path.Combine(dir, "staged-link");
        try
        {
            string real = Path.Combine(dir, "real-pkg");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "index.js"), "x");
            string link = Path.Combine(dir, "linked-pkg");
            Directory.CreateSymbolicLink(link, Path.Combine("real-pkg"));

            var logs = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(real) };
            int degraded = PluginStagingCopier.DegradeDirLink(link, target, logs.Add, visited);

            Assert.Equal(1, degraded);
            Assert.False(Directory.Exists(target));
            Assert.Contains(logs, l => l.Contains("遇环"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证「非权限异常仍 loud」不变量：建链目标已存在（IOException 形态）不得被降级路径吞掉
    /// 而须上抛（降级只捕 UnauthorizedAccessException；直测 internal 缝，同上 ADR）。</summary>
    [Fact]
    public void RecreateLink_NonPrivilegeFailure_ThrowsLoud()
    {
        string dir = OutsideDir("recreate-loud");
        try
        {
            string real = Path.Combine(dir, "real-pkg");
            Directory.CreateDirectory(real);
            string link = Path.Combine(dir, "linked-pkg");
            Directory.CreateSymbolicLink(link, Path.Combine("real-pkg"));
            string occupiedTarget = Path.Combine(dir, "occupied");
            Directory.CreateDirectory(occupiedTarget);

            Assert.ThrowsAny<System.IO.IOException>(() =>
                PluginStagingCopier.RecreateLink(link, occupiedTarget, _ => { }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>先摘链（不递归，避免递归删除与链接目标纠缠）再递归删 home。</summary>
    private static void UnlinkThenDeleteHome(string home, string link)
    {
        if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null)
        {
            Directory.Delete(link);
        }

        Directory.Delete(home, recursive: true);
    }
}
