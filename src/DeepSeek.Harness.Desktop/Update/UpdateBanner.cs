namespace DeepSeek.Harness.Desktop.Update;

/// <summary>
/// 自更新就绪横幅脚本（ADR shell-convenience-autostart-ready-notify）：ready 到达时一次性
/// 提示「新版本已就绪」。与既有横幅同款注入通道；幂等 id 守卫。
/// </summary>
public static class UpdateBanner
{
    /// <summary>生成 ready 横幅注入脚本（纯函数可单测）。</summary>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）。</param>
    public static string ReadyScript(string version, UiLocale? uiLocale = null)
    {
        string text = UiCopy.UpdateReadyText(version, uiLocale?.IsEnglish == true);
        return DesktopBanner.Build(
            "dsh-desktop-update-ready-banner",
            text,
            new DesktopBanner.Palette("#14251b", "#d9f2e3", "#1f3a2a", "#2f855a"),
            uiLocale?.OkLabel);
    }
}
