using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>有界恢复预算（ADR reference-alignment 批次五）：预算内允许 reload、耗尽转观测、
/// 成功恢复复位窗口。纯逻辑，与 <see cref="PageHealthTracker"/> 协同覆盖「同一死区有界多次恢复」。</summary>
public class PageHealthRecoveryTests
{
    [Fact]
    public void AllowsUpToLimit_ThenDenies()
    {
        var r = new PageHealthRecovery(maxAttempts: 3);

        Assert.True(r.TryAllowRecovery()); // #1
        Assert.True(r.TryAllowRecovery()); // #2
        Assert.True(r.TryAllowRecovery()); // #3
        Assert.False(r.TryAllowRecovery()); // 超出 → 拒绝
        Assert.Equal(3, r.Attempts);
        Assert.True(r.Exhausted);
    }

    [Fact]
    public void DeniedRequest_MarksExhausted_AndStaysDenied()
    {
        var r = new PageHealthRecovery(maxAttempts: 2);

        r.TryAllowRecovery();
        r.TryAllowRecovery();
        Assert.False(r.TryAllowRecovery());
        Assert.True(r.Exhausted);

        // 已耗尽：后续请求继续拒绝（leave 观测面，不会反复尝试）
        Assert.False(r.TryAllowRecovery());
        Assert.Equal(2, r.Attempts);
    }

    [Fact]
    public void MarkRecovered_ResetsBudget()
    {
        var r = new PageHealthRecovery(maxAttempts: 3);

        Assert.True(r.TryAllowRecovery());
        Assert.True(r.TryAllowRecovery());
        r.MarkRecovered(); // 成功恢复 → 复位窗口（新死区从头计）
        Assert.Equal(0, r.Attempts);
        Assert.False(r.Exhausted);
        Assert.True(r.TryAllowRecovery());
    }

    [Fact]
    public void RecoveredAfterExhaustion_AllowsNewEpisode()
    {
        var r = new PageHealthRecovery(maxAttempts: 1);

        Assert.True(r.TryAllowRecovery());
        Assert.False(r.TryAllowRecovery());
        Assert.True(r.Exhausted);

        // 死区结束后页回到 Alive：预算复位，新死区可重新有界恢复
        r.MarkRecovered();
        Assert.False(r.Exhausted);
        Assert.True(r.TryAllowRecovery());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMax_Throws(int maxAttempts)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new PageHealthRecovery(maxAttempts));
    }

    /// <summary>有界恢复与判定器协同：同一死区内 reload 后仍空白，可重凑阈值再次触发；
    /// 达上限转观测；Alive 恢复复位。这正是 PageHealthMonitor.HandleTransition 的驱动契约。</summary>
    [Fact]
    public void TrackerAndRecovery_CoexistWithinOneDeadEpisode()
    {
        var tracker = new PageHealthTracker(deadThreshold: 3);
        var recovery = new PageHealthRecovery(maxAttempts: 3);

        tracker.Record(PageHealth.Dead);
        tracker.Record(PageHealth.Dead);
        // 第 3 次 Dead → 迁移；预算内允许 reload #1
        tracker.Record(PageHealth.Dead);
        Assert.True(recovery.TryAllowRecovery());
        tracker.ReArm();

        // reload 后仍空白：重凑阈值 → reload #2
        Assert.Null(tracker.Record(PageHealth.Dead));
        Assert.Null(tracker.Record(PageHealth.Dead));
        tracker.Record(PageHealth.Dead);
        Assert.True(recovery.TryAllowRecovery());
        tracker.ReArm();

        // 再空白 → reload #3（达上限）
        tracker.Record(PageHealth.Dead);
        tracker.Record(PageHealth.Dead);
        tracker.Record(PageHealth.Dead);
        Assert.True(recovery.TryAllowRecovery());
        tracker.ReArm();

        // 第 4 次仍空白：超出预算 → 拒绝并转观测
        tracker.Record(PageHealth.Dead);
        tracker.Record(PageHealth.Dead);
        tracker.Record(PageHealth.Dead);
        Assert.False(recovery.TryAllowRecovery());
        Assert.True(recovery.Exhausted);

        // 页面最终回到 Alive：监测复位预算窗口
        recovery.MarkRecovered();
        Assert.Equal(0, recovery.Attempts);
        Assert.False(recovery.Exhausted);
    }
}
