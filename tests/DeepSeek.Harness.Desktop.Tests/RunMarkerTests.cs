using System.Text;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>HostLog 超限滚动。</summary>
public class HostLogRotationTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-logrot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RotateIfNeeded_OverLimit_RollsToOld_AndClearsCurrent()
    {
        var dir = NewDir();
        try
        {
            var log = Path.Combine(dir, "host.log");
            File.WriteAllBytes(log, new byte[HostLog.MaxBytes + 1]);

            HostLog.RotateIfNeeded(log);

            Assert.False(File.Exists(log));
            Assert.True(File.Exists(log + ".old"));
            // 小日志零动作
            File.WriteAllText(log, "tiny");
            HostLog.RotateIfNeeded(log);
            Assert.Equal("tiny", File.ReadAllText(log));
            Assert.True(File.Exists(log + ".old")); // 上一代保留
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RotateIfNeeded_MissingFile_NoOp()
    {
        var dir = NewDir();
        try
        {
            HostLog.RotateIfNeeded(Path.Combine(dir, "host.log"));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>崩溃取证 marker：占位/遗留判定/owner 清理/链接拒收。</summary>
public class RunMarkerTests
{
    [Fact]
    public void Acquire_FreshHome_NoUnclean_WritesMarker()
    {
        var home = NewDir();
        try
        {
            var result = RunMarker.Acquire(home);
            Assert.False(result.PreviousRunUnclean);
            Assert.True(File.Exists(RunMarker.MarkerPath(home)));
            Assert.Contains(result.Token, File.ReadAllText(RunMarker.MarkerPath(home)));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Acquire_AfterUnreleasedAcquire_DetectsUncleanExit()
    {
        var home = NewDir();
        try
        {
            var first = RunMarker.Acquire(home);
            var second = RunMarker.Acquire(home); // 上轮未 Release = 非受控退出
            Assert.True(second.PreviousRunUnclean);
            Assert.NotEqual(first.Token, second.Token);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Release_OwnerTokenRemoves_MismatchedTokenKeeps()
    {
        var home = NewDir();
        try
        {
            var marker = RunMarker.Acquire(home);
            Assert.False(RunMarker.Release(home, "not-the-owner"));
            Assert.True(File.Exists(RunMarker.MarkerPath(home)));
            Assert.True(RunMarker.Release(home, marker.Token));
            Assert.False(File.Exists(RunMarker.MarkerPath(home)));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Acquire_SymlinkedMarker_UnlinksAndReplaces_NotWrittenThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 符号链接行为按 Linux 断言
        }

        var home = NewDir();
        try
        {
            var outside = Path.Combine(Path.GetTempPath(), "dsh-marker-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            var dir = Path.Combine(home, "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(outside, "victim");
            File.CreateSymbolicLink(RunMarker.MarkerPath(home), outside);

            RunMarker.Acquire(home);

            var info = new FileInfo(RunMarker.MarkerPath(home));
            Assert.Null(info.LinkTarget); // 已替换为常规文件
            Assert.Equal("victim", File.ReadAllText(outside)); // 链接目标未被穿透改写
            File.Delete(outside);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
