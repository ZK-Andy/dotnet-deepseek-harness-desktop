namespace DeepSeek.Harness.Desktop.Tray;

/// <summary>
/// 托盘「检查更新」的结论文案（纯函数可单测）：菜单项本身没有界面，结论落页面横幅
/// （ADR avalonia-g3-update-surface：Avalonia 托盘没有通知面）；设置页状态行不受影响。
/// 下载进行中等中间态不发通知，避免过程噪音。
/// 文案随宿主 UI 语言（字面量单一事实源在 <see cref="UiCopy"/>）。
/// </summary>
internal static class TrayCheckFeedback
{
    /// <summary>检查结束态 → 通知正文；返回 null 表示此态不打扰。</summary>
    /// <param name="state">检查结束态。</param>
    /// <param name="english">是否取英文分支（宿主 UI 语言单点）。</param>
    public static string? Message(UpdateState state, bool english) => state.Status switch
    {
        UpdateStatus.UpToDate => UiCopy.TrayCheckUpToDate(english),
        UpdateStatus.Ready => UiCopy.TrayCheckReady(state.Version ?? string.Empty, english),
        UpdateStatus.Error => UiCopy.TrayCheckFailedPrefix(english)
            + (state.Message ?? UiCopy.RecoveryUnknownReason(english)),
        _ => null,
    };
}
