namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>遗留原生对话框收割（ADR failure-surface-dialog-defects）：stat 解析（括号内 comm + 亲缘 ppid）
/// 与对话框进程名判定经纯函数单测；<c>Reap</c> 本体是进程 seam（只读扫描 + 只杀本进程直接子进程），
/// 另有一条全平台无抛测试探活（测试环境无匹配进程，Linux CI 只走读扫描）。</summary>
public class DialogOrphanReaperTests
{
    /// <summary>stat 解析：进程名 + 父 pid；comm 含空格与括号时仍以首 ( 到末 ) 为准。</summary>
    [Fact]
    public void TryParseStat_ParsesCommAndParentPid()
    {
        Assert.True(DialogOrphanReaper.TryParseStat(
            "1234 (zenity) S 5678 1234 5678 0 -1 4194304 0 0 0 0 0 0 0 0 0 0 20 0 1 0 1 0 0 0",
            out string comm,
            out int ppid));
        Assert.Equal("zenity", comm);
        Assert.Equal(5678, ppid);

        Assert.True(DialogOrphanReaper.TryParseStat("42 (Web Content) S 1 42 1 0 -1 0 0 0 0 0 0 0 0 0 0 0 20 0 1 0 1 0 0 0", out comm, out ppid));
        Assert.Equal("Web Content", comm);
        Assert.Equal(1, ppid);

        Assert.True(DialogOrphanReaper.TryParseStat("99 (foo(bar)) R 7 99 7 0 -1 0 0 0 0 0 0 0 0 0 0 0 20 0 1 0 1 0 0 0", out comm, out ppid));
        Assert.Equal("foo(bar)", comm);
        Assert.Equal(7, ppid);
    }

    /// <summary>stat 解析：无括号/空名/ppid 非数字一律失败（失败即「不是收割对象」，零误杀方向）。</summary>
    [Theory]
    [InlineData("no parens here")]
    [InlineData("12 () S 34 12 34 0 -1 0")]
    [InlineData("5 (bash) S xx 5 1 0 -1 0")]
    [InlineData("")]
    public void TryParseStat_RejectsMalformed(string stat)
    {
        Assert.False(DialogOrphanReaper.TryParseStat(stat, out _, out _));
    }

    /// <summary>对话框进程名判定：精确匹配 zenity/kdialog（与插件候选序同源）。</summary>
    [Theory]
    [InlineData("zenity")]
    [InlineData("kdialog")]
    public void IsDialogProcessName_MatchesExactToolNames(string comm)
    {
        Assert.True(DialogOrphanReaper.IsDialogProcessName(comm));
    }

    /// <summary>对话框进程名判定：大小写/前后缀/他者一律不认（精确匹配，零误杀方向）。</summary>
    [Theory]
    [InlineData("bash")]
    [InlineData("Zenity")]
    [InlineData("zenity-extra")]
    [InlineData("osascript")]
    public void IsDialogProcessName_RejectsOthers(string comm)
    {
        Assert.False(DialogOrphanReaper.IsDialogProcessName(comm));
    }

    /// <summary>收割入口全平台无抛：非 Linux 直接 no-op；Linux 下测试环境无匹配子进程，只走读扫描。</summary>
    [Fact]
    public void Reap_DoesNotThrow()
    {
        var reaper = new DialogOrphanReaper();
        Exception? exception = Record.Exception(() => reaper.Reap());
        Assert.Null(exception);
    }
}
