namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>焦点兜底复验决策（ADR 2026-10-28-first-map-input-focus-recheck）：以键盘焦点本体
/// <c>XGetInputFocus</c> 为唯一收口判据——代理指标（EWMH 活动窗）与本体背离时不得放行。</summary>
public class X11FocusBootstrapDecideTests
{
    private static readonly IntPtr s_self = new(0x80000f);
    private static readonly IntPtr s_ghostWindow = new(0x400003);

    /// <summary>验证本体到位即收口：活动窗是否指向本窗不参与判定（健康态与代理先中态同出口）。</summary>
    [Fact]
    public void InputFocusOnSelf_Reaches_EvenWhenActiveWindowDisagrees()
    {
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.Reached,
            X11FocusBootstrap.Decide(s_self, s_ghostWindow, s_self));
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.Reached,
            X11FocusBootstrap.Decide(s_self, s_self, s_self));
    }

    /// <summary>验证活动窗仍非本窗：维持既有路径补发 EWMH 激活请求，不直落。</summary>
    [Fact]
    public void ActiveWindowNotSelf_ResendsActivate()
    {
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.ResendActivate,
            X11FocusBootstrap.Decide(s_self, s_ghostWindow, s_ghostWindow));
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.ResendActivate,
            X11FocusBootstrap.Decide(s_self, IntPtr.Zero, IntPtr.Zero));
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.ResendActivate,
            X11FocusBootstrap.Decide(s_self, s_ghostWindow, new IntPtr(1)));
    }

    /// <summary>验证 EWMH/本体背离态（10-28 活体证据：活动窗已指向本窗、键盘仍废）走直落分支，
    /// 代理指标不得放行——本例即修复前复验读 <c>_NET_ACTIVE_WINDOW</c> 误判成功的形态。</summary>
    [Fact]
    public void ActiveWindowSelfButInputFocusElsewhere_SetsInputFocusDirect()
    {
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.SetInputFocusDirect,
            X11FocusBootstrap.Decide(s_self, s_self, s_ghostWindow));
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.SetInputFocusDirect,
            X11FocusBootstrap.Decide(s_self, s_self, IntPtr.Zero));
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.SetInputFocusDirect,
            X11FocusBootstrap.Decide(s_self, s_self, new IntPtr(1)));
    }

    /// <summary>验证后代持焦是合法态（R2 S2：复验窗内 WebView 子窗被点击取焦不抢回）：后代标记
    /// 置位即收口，与活动窗指向无关；标记缺省（未查子树）时同形态仍走直落。</summary>
    [Fact]
    public void InputFocusInSubtree_Reaches()
    {
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.Reached,
            X11FocusBootstrap.Decide(s_self, s_self, s_ghostWindow, inputFocusInSubtree: true));
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.Reached,
            X11FocusBootstrap.Decide(s_self, s_ghostWindow, s_ghostWindow, inputFocusInSubtree: true));
        Assert.Equal(
            X11FocusBootstrap.FocusRecoveryAction.SetInputFocusDirect,
            X11FocusBootstrap.Decide(s_self, s_self, s_ghostWindow, inputFocusInSubtree: false));
    }
}
