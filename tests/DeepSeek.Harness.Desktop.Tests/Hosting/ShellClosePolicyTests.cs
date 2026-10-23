namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>关窗裁决：只有「未批准退出」且「托盘就绪」且「偏好隐藏」才隐藏到托盘，其余一律放行关窗。</summary>
public class ShellClosePolicyTests
{
    /// <summary>验证托盘就绪、偏好隐藏且退出未批准时隐藏（关窗不结束进程）。</summary>
    [Fact]
    public void TrayReadyAndExitNotApproved_Hides()
    {
        Assert.True(ShellClosePolicy.ShouldHide(
            exitApproved: false, trayReady: true, hideOnClose: true, platformInitiated: false));
    }

    /// <summary>验证托盘退出路径（已批准）放行关窗，判据不被隐藏分支吃掉。</summary>
    [Fact]
    public void ExitApproved_Closes_EvenWithTray()
    {
        Assert.False(ShellClosePolicy.ShouldHide(
            exitApproved: true, trayReady: true, hideOnClose: true, platformInitiated: false));
    }

    /// <summary>验证托盘不可用时放行关窗：隐藏等于把窗口弄丢（无图标可召回）。</summary>
    [Fact]
    public void TrayUnavailable_Closes()
    {
        Assert.False(ShellClosePolicy.ShouldHide(
            exitApproved: false, trayReady: false, hideOnClose: true, platformInitiated: false));
    }

    /// <summary>验证平台注销/关机放行关闭：即使托盘就绪且未批准退出，也不得隐藏劫持系统注销。</summary>
    [Fact]
    public void PlatformInitiatedClose_IsNeverHijacked()
    {
        Assert.False(ShellClosePolicy.ShouldHide(
            exitApproved: false, trayReady: true, hideOnClose: true, platformInitiated: true));
    }

    /// <summary>验证两者同时成立（已批准且无托盘）仍然放行，不出现退出被拦。</summary>
    [Fact]
    public void ExitApprovedAndNoTray_Closes()
    {
        Assert.False(ShellClosePolicy.ShouldHide(
            exitApproved: true, trayReady: false, hideOnClose: true, platformInitiated: false));
    }

    /// <summary>验证关窗直接退出偏好（desktop.closeToTray.set false）放行关窗，托盘就绪也不隐藏。</summary>
    [Fact]
    public void HidePreferenceOff_Closes_EvenWithTrayReady()
    {
        Assert.False(ShellClosePolicy.ShouldHide(
            exitApproved: false, trayReady: true, hideOnClose: false, platformInitiated: false));
    }
}
