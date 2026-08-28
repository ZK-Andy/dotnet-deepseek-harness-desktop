namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 自更新就绪横幅脚本（ADR shell-convenience-autostart-ready-notify）：ready 到达时一次性
/// 提示「新版本已就绪」。与既有横幅同款注入通道；幂等 id 守卫。
/// </summary>
public static class UpdateBanner
{
    /// <summary>生成 ready 横幅注入脚本（纯函数可单测）。</summary>
    public static string ReadyScript(string version)
    {
        var text = "新版本 " + version + " 已就绪，可在 设置 → 桌面设置 中一键安装。";
        return DesktopBanner.Build(
            "dsh-desktop-update-ready-banner",
            text,
            new DesktopBanner.Palette("#14251b", "#d9f2e3", "#1f3a2a", "#2f855a"));
    }
}
