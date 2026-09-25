namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>收养免导航裁决（ADR adopt-skip-navigate-on-self-reload）：周期起点后的到达即跳过壳侧导航。</summary>
public class AdoptNavigateGateTests
{
    /// <summary>从未到达（null）→ 不跳过：走既有导航兜底（非市场页场景）。</summary>
    [Fact]
    public void NeverNavigated_DoesNotSkip()
    {
        var start = new DateTimeOffset(2026, 9, 25, 20, 0, 52, TimeSpan.Zero);

        Assert.False(AdoptNavigateGate.ShouldSkipAdoptNavigate(null, start));
    }

    /// <summary>到达早于周期起点 → 不跳过：那是上个周期的导航，与本次自刷无关。</summary>
    [Fact]
    public void NavigatedBeforeCycle_DoesNotSkip()
    {
        var start = new DateTimeOffset(2026, 9, 25, 20, 0, 52, TimeSpan.Zero);

        Assert.False(AdoptNavigateGate.ShouldSkipAdoptNavigate(start.AddSeconds(-1), start));
    }

    /// <summary>到达恰为周期起点 → 跳过：边界取闭区间，迟到一拍的恢复屏标记不漏掉同刻到达。</summary>
    [Fact]
    public void NavigatedAtCycleStart_Skips()
    {
        var start = new DateTimeOffset(2026, 9, 25, 20, 0, 52, TimeSpan.Zero);

        Assert.True(AdoptNavigateGate.ShouldSkipAdoptNavigate(start, start));
    }

    /// <summary>到达晚于周期起点 → 跳过：页内自刷（市场 doRestart 的 location.reload）。</summary>
    [Fact]
    public void NavigatedAfterCycleStart_Skips()
    {
        var start = new DateTimeOffset(2026, 9, 25, 20, 0, 52, TimeSpan.Zero);

        Assert.True(AdoptNavigateGate.ShouldSkipAdoptNavigate(start.AddSeconds(5), start));
    }
}
