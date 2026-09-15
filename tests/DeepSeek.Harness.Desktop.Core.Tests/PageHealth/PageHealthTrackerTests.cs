
using PageHealthState = DeepSeek.Harness.Desktop.Core.PageHealth.PageHealth;

namespace DeepSeek.Harness.Desktop.Core.Tests.PageHealth;

/// <summary>页面健康判定核心（ADR page-health-monitor 阶段 1）：Dead 去抖、Unknown 不计数、
/// Alive 复位；迁移描述只在状态翻转时产生——观测面绝无自动动作。</summary>
public class PageHealthTrackerTests
{
    /// <summary>验证首个 Alive 样本使状态从 Unknown 迁移到 Alive 并产出迁移描述。</summary>
    [Fact]
    public void FirstAlive_Transitions_FromUnknown()
    {
        var t = new PageHealthTracker();

        Assert.Null(t.Record(PageHealthState.Unknown));
        Assert.Equal("页面健康：alive", t.Record(PageHealthState.Alive));
        Assert.Equal(PageHealthState.Alive, t.Current);
    }

    /// <summary>验证 Dead 需连续凑满阈值（3 次）才迁移到 Dead 态，前两次不产出迁移描述。</summary>
    [Fact]
    public void Dead_RequiresConsecutiveSamples_BeforeTransition()
    {
        var t = new PageHealthTracker(deadThreshold: 3);

        t.Record(PageHealthState.Alive);
        Assert.Null(t.Record(PageHealthState.Dead));
        Assert.Equal(PageHealthState.Alive, t.Current);
        Assert.Null(t.Record(PageHealthState.Dead));
        Assert.Equal("页面健康：连续 3 次探针为空（dead）", t.Record(PageHealthState.Dead));
        Assert.Equal(PageHealthState.Dead, t.Current);
    }

    /// <summary>验证 Unknown 样本既不累计 Dead 计数也不清零，Dead 连击不受打断继续累计。</summary>
    [Fact]
    public void Unknown_DoesNotCount_NorReset()
    {
        var t = new PageHealthTracker(deadThreshold: 3);

        t.Record(PageHealthState.Alive);
        t.Record(PageHealthState.Dead);
        t.Record(PageHealthState.Unknown); // 不累计也不清零
        Assert.Null(t.Record(PageHealthState.Dead)); // 第 2 次 Dead
        Assert.Equal(PageHealthState.Alive, t.Current);
        Assert.NotNull(t.Record(PageHealthState.Dead)); // 第 3 次 Dead → 迁移
    }

    /// <summary>验证 Alive 样本打断 Dead 连击并复位计数，复位后需重新凑满 3 次 Dead 才再迁移。</summary>
    [Fact]
    public void Alive_ResetsDeadStreak_AndRecovers()
    {
        var t = new PageHealthTracker(deadThreshold: 3);

        t.Record(PageHealthState.Alive);
        t.Record(PageHealthState.Dead);
        t.Record(PageHealthState.Dead);
        // Alive 打断连击：仍处 Alive 态无迁移，且计数复位
        Assert.Null(t.Record(PageHealthState.Alive));
        Assert.Equal(PageHealthState.Alive, t.Current);

        // 复位后需重新凑满 3 次 Dead 才迁移
        t.Record(PageHealthState.Dead);
        t.Record(PageHealthState.Dead);
        Assert.Null(t.Record(PageHealthState.Unknown));
        Assert.Equal("页面健康：连续 3 次探针为空（dead）", t.Record(PageHealthState.Dead));
    }

    /// <summary>验证有效探针计数只统计非 Unknown 样本，窗口未就绪期的 Unknown 轮询不计数。</summary>
    [Fact]
    public void ProbeCount_CountsValidProbesOnly_UnknownExcluded()
    {
        var t = new PageHealthTracker();

        t.Record(PageHealthState.Unknown); // 窗口未就绪期异常轮询不计入有效探针
        t.Record(PageHealthState.Alive);
        t.Record(PageHealthState.Unknown);

        Assert.Equal(1, t.ProbeCount);
    }

    /// <summary>验证 ReArm 使状态复位为 Unknown 并清空 Dead 连击，此后需重新凑满阈值且 ProbeCount 保持累计。</summary>
    [Fact]
    public void ReArm_ResetsToUnknown_AndClearsDeadStreak()
    {
        var t = new PageHealthTracker(deadThreshold: 3);

        t.Record(PageHealthState.Dead);
        t.Record(PageHealthState.Dead);
        t.Record(PageHealthState.Dead);
        Assert.Equal(PageHealthState.Dead, t.Current);

        // 一次有界 reload 后 ReArm：死区重置——reload 后仍为空的页面需重新凑满阈值再触发迁移
        t.ReArm();
        Assert.Equal(PageHealthState.Unknown, t.Current);

        // 复位后需全新凑满 3 次 Dead 才再迁移，且 ProbeCount 不重置（诊断累计）
        int probesBefore = t.ProbeCount;
        Assert.Null(t.Record(PageHealthState.Dead));
        Assert.Null(t.Record(PageHealthState.Dead));
        Assert.Equal("页面健康：连续 3 次探针为空（dead）", t.Record(PageHealthState.Dead));
        Assert.Equal(PageHealthState.Dead, t.Current);
        Assert.Equal(probesBefore + 3, t.ProbeCount);
    }
}
