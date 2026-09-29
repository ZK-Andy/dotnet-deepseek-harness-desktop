
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>残留收敛预检（OrphanDshReaper.EnsureNoResidue）决策契约（ADR residue-lock-fail-loud +
/// pid-reuse-foreign-cmdline-clear）：无记录/已死放行，复验命中杀，杀不掉或活着且验不出归属一律
/// Unreapable 且留痕（fail loud），绝不杀验不明的 pid（零误杀）；唯一放行例外 = 命令行可证 PID 已被
/// 无关进程复用（<see cref="OrphanDshReaper.PlausiblyOwnRuntime"/> 甄别为异己），此时清脏记录按 Clear 收敛。</summary>
public class OrphanDshReaperEnsureTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-ensure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteSpawnRecord(string dir, int pid, string token)
    {
        string path = Path.Combine(dir, ".dsh-pid");
        File.WriteAllLines(path, new[] { pid.ToString(), token });
        return path;
    }

    /// <summary>验证无 PID 记录时直接 Clear，且不触碰任何委托。</summary>
    [Fact]
    public void Ensure_NoRecord_ReturnsClear()
    {
        string dir = NewDir();
        try
        {
            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                Path.Combine(dir, ".dsh-pid"),
                readToken: _ => throw new InvalidOperationException("must not be called"),
                isAlive: _ => throw new InvalidOperationException("must not be called"),
                killTree: _ => throw new InvalidOperationException("must not be called"),
                log: _ => throw new InvalidOperationException("must not be called"));

            Assert.Equal(OrphanDshReaper.ResidueState.Clear, state);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证 token 复验命中、杀后已死时返回 Reaped。</summary>
    [Fact]
    public void Ensure_TokenMatches_KilledAndDead_ReturnsReaped()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");
            int killed = 0;

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => "abc123",
                isAlive: _ => false,
                killTree: p => { killed = p; },
                log: _ => { });

            Assert.Equal(OrphanDshReaper.ResidueState.Reaped, state);
            Assert.Equal(4242, killed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证 token 命中但杀后仍活时返回 Unreapable 且留痕（杀不掉即 fail loud）。</summary>
    [Fact]
    public void Ensure_TokenMatches_StillAlive_ReturnsUnreapable()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");
            var logs = new List<string>();

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => "abc123",
                isAlive: _ => true,
                killTree: _ => { },
                logs.Add);

            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, state);
            Assert.NotEmpty(logs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证杀器抛异常时返回 Unreapable（不吞异常面，按杀不掉收敛）。</summary>
    [Fact]
    public void Ensure_KillThrows_ReturnsUnreapable()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");
            var logs = new List<string>();

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => "abc123",
                isAlive: _ => true,
                killTree: _ => throw new InvalidOperationException("kill failed"),
                logs.Add);

            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, state);
            Assert.NotEmpty(logs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证 token 不中但记录 pid 已死时返回 Clear 且不杀（陈旧记录不挡启动）。</summary>
    [Fact]
    public void Ensure_TokenMismatch_Dead_ReturnsClearWithoutKilling()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "expected-token");
            int killed = 0;

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => "different-token",
                isAlive: _ => false,
                killTree: p => { killed = p; },
                log: _ => { });

            Assert.Equal(OrphanDshReaper.ResidueState.Clear, state);
            Assert.Equal(0, killed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证 token 不中但记录 pid 仍活时返回 Unreapable 且绝不杀（零误杀核心：Windows 无 /proc 复验即此分支）。</summary>
    [Fact]
    public void Ensure_TokenMismatch_Alive_ReturnsUnreapableWithoutKilling()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "expected-token");
            int killed = 0;
            var logs = new List<string>();

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => null, // 非 Linux 读不到环境：恒 null
                isAlive: _ => true,
                killTree: p => { killed = p; },
                logs.Add);

            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, state);
            Assert.Equal(0, killed);
            Assert.NotEmpty(logs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证复验委托抛异常时按验不明收敛：活着即 Unreapable，死了即 Clear。</summary>
    [Fact]
    public void Ensure_ReadTokenThrows_FallsBackToAliveCheck()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");

            OrphanDshReaper.ResidueState alive = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => throw new IOException("proc unreadable"),
                isAlive: _ => true,
                killTree: _ => throw new InvalidOperationException("must not be called"),
                log: _ => { });
            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, alive);

            OrphanDshReaper.ResidueState dead = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => throw new IOException("proc unreadable"),
                isAlive: _ => false,
                killTree: _ => throw new InvalidOperationException("must not be called"),
                log: _ => { });
            Assert.Equal(OrphanDshReaper.ResidueState.Clear, dead);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证判活委托抛异常时按仍活收敛为 Unreapable（AliveOrLoud 的 fail-loud 分支钉子）。</summary>
    [Fact]
    public void Ensure_IsAliveThrows_ReturnsUnreapable()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");
            var logs = new List<string>();

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => "abc123",
                isAlive: _ => throw new InvalidOperationException("ps unreadable"),
                killTree: _ => { },
                logs.Add);

            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, state);
            Assert.NotEmpty(logs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证命中已杀但 Reap 报未杀、且 pid 已死时收敛为 Clear（reaped==false 的诚实分支钉子）。</summary>
    [Fact]
    public void Ensure_ReapReportsFalse_Dead_ReturnsClear()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");

            // killTree 抛 → Reap 内部吞后报 false；pid 已死 → Clear（非 Unreapable）
            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => "abc123",
                isAlive: _ => false,
                killTree: _ => throw new InvalidOperationException("kill failed"),
                log: _ => { });

            Assert.Equal(OrphanDshReaper.ResidueState.Clear, state);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>PID 复用甄别（ADR pid-reuse-foreign-cmdline-clear）：活着但验不明、且命令行可证为无关
    /// 进程（实机事故形态：GNOME localsearch-3 撞上跨重启 stale 记录）时收敛为 Clear、清脏记录、绝不杀。</summary>
    [Fact]
    public void Ensure_AliveUnverified_ForeignCmdline_ClearsStaleRecordWithoutKill()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");
            var logs = new List<string>();

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => null, // environ 无 token：token 复验不中（复用后的无关进程没有我方标记）
                isAlive: _ => true,
                killTree: _ => throw new InvalidOperationException("must not be called"),
                logs.Add,
                readCommandLine: _ => "/usr/libexec/localsearch-3 --gdbus-service");

            Assert.Equal(OrphanDshReaper.ResidueState.Clear, state);
            Assert.False(File.Exists(pidPath), "过期记录应随放行清掉，否则监督器每轮重走甄别");
            Assert.Contains(logs, l => l.Contains("已被无关进程复用"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>命令行形似自家 runtime（node 执行 dsh bin 的生产形态）时不得放行：保持 Unreapable 且记录保留。</summary>
    [Fact]
    public void Ensure_AliveUnverified_OwnShapedCmdline_StaysUnreapable()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => null,
                isAlive: _ => true,
                killTree: _ => throw new InvalidOperationException("must not be called"),
                log: _ => { },
                readCommandLine: _ => "node /home/zk/.local/lib/node_modules/@deepseek-ai/dsh/lib/bin.js web --profile dotnet-desktop");

            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, state);
            Assert.True(File.Exists(pidPath), "存疑残留记录必须保留，交给人工确认");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>命令行读不到（null）时甄别不了：保持补丁前行为 Unreapable（fail loud 不放行）。</summary>
    [Fact]
    public void Ensure_AliveUnverified_UnreadableCmdline_StaysUnreapable()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => null,
                isAlive: _ => true,
                killTree: _ => throw new InvalidOperationException("must not be called"),
                log: _ => { },
                readCommandLine: _ => null);

            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, state);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>甄别委托抛异常按「证不了他者」收敛为 Unreapable（与判活同向的 fail-loud 钉子）。</summary>
    [Fact]
    public void Ensure_ReadCommandLineThrows_TreatedAsUnverified()
    {
        string dir = NewDir();
        try
        {
            string pidPath = WriteSpawnRecord(dir, 4242, "abc123");

            OrphanDshReaper.ResidueState state = OrphanDshReaper.EnsureNoResidue(
                pidPath,
                readToken: _ => null,
                isAlive: _ => true,
                killTree: _ => throw new InvalidOperationException("must not be called"),
                log: _ => { },
                readCommandLine: _ => throw new IOException("proc vanished"));

            Assert.Equal(OrphanDshReaper.ResidueState.Unreapable, state);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>自家 runtime 形态白名单（保守窄集）：node/dsh 本体与后段 dsh 脚本命中；无关 exe 不命中；
    /// 空形（不可证他者）按自家处理。</summary>
    [Fact]
    public void PlausiblyOwnRuntime_WhitelistShape()
    {
        Assert.True(OrphanDshReaper.PlausiblyOwnRuntime(
            "node /home/zk/.local/lib/node_modules/@deepseek-ai/dsh/lib/bin.js web --profile dotnet-desktop"));
        Assert.True(OrphanDshReaper.PlausiblyOwnRuntime("/usr/bin/dsh web --port 0"));
        Assert.True(OrphanDshReaper.PlausiblyOwnRuntime("node")); // 裸 node（argv[0] 命中即存疑）
        Assert.True(OrphanDshReaper.PlausiblyOwnRuntime("bash -c exec dsh")); // 后段 dsh 脚本路径
        Assert.True(OrphanDshReaper.PlausiblyOwnRuntime(string.Empty)); // 空形：不可证他者，保守按自家

        Assert.False(OrphanDshReaper.PlausiblyOwnRuntime("/usr/libexec/localsearch-3 --gdbus-service"));
        Assert.False(OrphanDshReaper.PlausiblyOwnRuntime("/usr/bin/python3 -m http.server"));
        Assert.False(OrphanDshReaper.PlausiblyOwnRuntime("/usr/sbin/nginx"));
    }
}
