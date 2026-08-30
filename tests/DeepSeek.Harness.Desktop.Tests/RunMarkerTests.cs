using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>HostLog 超限滚动。</summary>
public class HostLogRotationTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-logrot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>验证超限日志滚动为 .old 并清空当前文件；小日志零动作且上一代 .old 保留。</summary>
    [Fact]
    public void RotateIfNeeded_OverLimit_RollsToOld_AndClearsCurrent()
    {
        string dir = NewDir();
        try
        {
            string log = Path.Combine(dir, "host.log");
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

    /// <summary>验证日志文件缺失时滚动为空操作，目录不产生任何新文件。</summary>
    [Fact]
    public void RotateIfNeeded_MissingFile_NoOp()
    {
        string dir = NewDir();
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
    /// <summary>验证全新 home 获取 marker：PreviousRunUnclean 为 false，且 token 落盘于 marker 文件。</summary>
    [Fact]
    public void Acquire_FreshHome_NoUnclean_WritesMarker()
    {
        string home = NewDir();
        try
        {
            RunMarkerResult result = RunMarker.Acquire(home);
            Assert.False(result.PreviousRunUnclean);
            Assert.True(File.Exists(RunMarker.MarkerPath(home)));
            Assert.Contains(result.Token, File.ReadAllText(RunMarker.MarkerPath(home)));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证上轮未 Release 即再次 Acquire 判定非受控退出（PreviousRunUnclean=true）并换发新 token。</summary>
    [Fact]
    public void Acquire_AfterUnreleasedAcquire_DetectsUncleanExit()
    {
        string home = NewDir();
        try
        {
            RunMarkerResult first = RunMarker.Acquire(home);
            RunMarkerResult second = RunMarker.Acquire(home); // 上轮未 Release = 非受控退出
            Assert.True(second.PreviousRunUnclean);
            Assert.NotEqual(first.Token, second.Token);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>验证非 owner token 的 Release 被拒且 marker 保留，owner token 的 Release 删除 marker 文件。</summary>
    [Fact]
    public void Release_OwnerTokenRemoves_MismatchedTokenKeeps()
    {
        string home = NewDir();
        try
        {
            RunMarkerResult marker = RunMarker.Acquire(home);
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

    /// <summary>验证 marker 为符号链接时先解链替换为常规文件、绝不穿透改写链接目标（仅 Linux 断言）。</summary>
    [Fact]
    public void Acquire_SymlinkedMarker_UnlinksAndReplaces_NotWrittenThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 符号链接行为按 Linux 断言
        }

        string home = NewDir();
        try
        {
            string outside = Path.Combine(Path.GetTempPath(), "dsh-marker-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            string dir = Path.Combine(home, "logs");
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

    /// <summary>验证 marker 文件为可解析 JSON 且含 token/pid/startedAt 三个契约键，startedAt 为合法时间戳。</summary>
    [Fact]
    public void Acquire_MarkerFile_HasParseableContractKeys()
    {
        string home = NewDir();
        try
        {
            RunMarkerResult result = RunMarker.Acquire(home);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RunMarker.MarkerPath(home)));
            JsonElement root = doc.RootElement;
            Assert.Equal(result.Token, root.GetProperty("token").GetString());
            Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
            Assert.True(DateTimeOffset.TryParse(root.GetProperty("startedAt").GetString(), out _));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>验证 en 语言下未受控退出横幅输出英文文案与 textContent="OK" 按钮，不残留中文。</summary>
    [Fact]
    public void UncleanBannerScript_LocalizesEnglish()
    {
        // 宿主横幅双语（ADR host-ui-locale）：en 出英文文案与 OK 按钮
        var en = new UiLocale();
        en.Set("en");
        string script = RunMarker.UncleanBannerScript(en);
        Assert.Contains("did not exit cleanly", script);
        Assert.Contains("textContent=\"OK\"", script);
        Assert.DoesNotContain("上次运行未正常退出", script);
    }
}
