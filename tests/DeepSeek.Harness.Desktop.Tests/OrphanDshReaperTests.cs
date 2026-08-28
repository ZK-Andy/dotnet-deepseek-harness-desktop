using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>孤儿 dsh 清扫器（OrphanDshReaper）决策契约：token 复验匹配才杀，否则一律不杀（零误杀）。</summary>
public class OrphanDshReaperTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-reaper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteSpawnRecord(string dir, int pid, string token)
    {
        var path = Path.Combine(dir, ".dsh-pid");
        File.WriteAllLines(path, new[] { pid.ToString(), token });
        return path;
    }

    [Fact]
    public void Reap_TokenMatches_KillsTree()
    {
        var dir = NewDir();
        try
        {
            var pidPath = WriteSpawnRecord(dir, 4242, "abc123");
            var killed = 0;

            var reaped = OrphanDshReaper.Reap(
                pidPath,
                readToken: _ => "abc123",
                killTree: p => { killed = p; },
                log: _ => { });

            Assert.True(reaped);
            Assert.Equal(4242, killed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reap_TokenMismatch_DoesNotKill()
    {
        // PID 复用指向无关进程：token 复验失败 → 绝不杀（零误杀核心)
        var dir = NewDir();
        try
        {
            var pidPath = WriteSpawnRecord(dir, 4242, "expected-token");
            var killed = 0;

            var reaped = OrphanDshReaper.Reap(
                pidPath,
                readToken: _ => "different-token", // 进程环境里是别的 token
                killTree: p => { killed = p; },
                log: _ => { });

            Assert.False(reaped);
            Assert.Equal(0, killed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reap_TokenUnreadable_DoesNotKill()
    {
        // 进程已死 / 非 Linux 读不到环境：readToken 返 null → 不杀
        var dir = NewDir();
        try
        {
            var pidPath = WriteSpawnRecord(dir, 4242, "token");
            var killed = 0;

            var reaped = OrphanDshReaper.Reap(
                pidPath,
                readToken: _ => null,
                killTree: p => { killed = p; },
                log: _ => { });

            Assert.False(reaped);
            Assert.Equal(0, killed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reap_PidFileMissing_ReturnsFalse()
    {
        var dir = NewDir();
        try
        {
            var missing = Path.Combine(dir, "does-not-exist");
            var reaped = OrphanDshReaper.Reap(
                missing,
                readToken: _ => "token",
                killTree: _ => { },
                log: _ => { });

            Assert.False(reaped);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reap_PidFileCorrupt_ReturnsFalse()
    {
        // 记录损坏不可读：按无可清扫（fail-safe，不挡启动）
        var dir = NewDir();
        try
        {
            var pidPath = Path.Combine(dir, ".dsh-pid");
            File.WriteAllText(pidPath, "not-a-pid");

            var reaped = OrphanDshReaper.Reap(
                pidPath,
                readToken: _ => "token",
                killTree: _ => { },
                log: _ => { });

            Assert.False(reaped);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reap_KillThrows_ReturnsFalse()
    {
        // 杀失败（进程恰好退出/无权限）：记日志返回 false，不向上抛，不挡启动
        var dir = NewDir();
        try
        {
            var pidPath = WriteSpawnRecord(dir, 4242, "token");
            var reaped = OrphanDshReaper.Reap(
                pidPath,
                readToken: _ => "token",
                killTree: _ => throw new InvalidOperationException("进程已退出"),
                log: _ => { });

            Assert.False(reaped);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
