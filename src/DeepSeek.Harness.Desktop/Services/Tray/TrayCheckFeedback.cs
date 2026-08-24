namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 托盘「检查更新」的结果通知文案（纯函数可单测）：菜单项本身没有界面，检查结论经
/// 系统托盘通知反馈（Linux notify-send / Windows 气泡 / macOS osascript）；设置页状态行
/// 不受影响。下载进行中等中间态不发通知，避免过程噪音。
/// </summary>
internal static class TrayCheckFeedback
{
    /// <summary>通知标题。</summary>
    public const string Title = "DeepSeek Harness Desktop";

    /// <summary>检查结束态 → 通知正文；返回 null 表示此态不打扰。</summary>
    public static string? Message(Update.UpdateState state) => state.Status switch
    {
        Update.UpdateStatus.UpToDate => "已是最新版本",
        Update.UpdateStatus.Ready => $"新版本 {state.Version} 已就绪，可在 设置 → 桌面设置 安装",
        Update.UpdateStatus.Error => "检查更新失败：" + (state.Message ?? "未知原因"),
        _ => null,
    };
}
