
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>残留收敛预检（OrphanDshReaper.EnsureNoResidue）决策契约（ADR residue-lock-fail-loud）：
/// 无记录/已死放行，复验命中杀，杀不掉或活着但验不明一律 Unreapable 且留痕（fail loud），绝不杀验不明的 pid（零误杀）。</summary>
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
}
