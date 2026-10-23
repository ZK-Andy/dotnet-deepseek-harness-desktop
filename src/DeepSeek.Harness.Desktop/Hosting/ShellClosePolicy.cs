namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>关窗裁决（纯判据，可单测）：只有「平台未发起关闭」且「未批准退出」且「托盘就绪」且
/// 「用户偏好隐藏」时才把关窗转成隐藏到托盘；其余一律放行关窗——平台注销/关机绝不能被隐藏劫持。
/// 偏好来自 <c>desktop.closeToTray</c>（缺省 true，与历史行为一致）。</summary>
internal static class ShellClosePolicy
{
    /// <summary>是否应拦截本次关窗并隐藏到托盘。</summary>
    /// <param name="exitApproved">关窗闸门是否已批准退出（托盘「退出」路径）。</param>
    /// <param name="trayReady">托盘是否可用（不可用时隐藏等于把窗口弄丢）。</param>
    /// <param name="hideOnClose">用户是否选了「关闭时最小化到托盘」。</param>
    /// <param name="platformInitiated">本次关闭是否由平台发起（注销/关机）。</param>
    /// <returns>true = 取消关窗并隐藏；false = 放行关窗。</returns>
    public static bool ShouldHide(bool exitApproved, bool trayReady, bool hideOnClose, bool platformInitiated) =>
        !platformInitiated && !exitApproved && trayReady && hideOnClose;
}
