namespace DeepSeek.Harness.Desktop.Tray;

/// <summary>托盘菜单条目的本地模型（与托盘框架无关）：id / 文案 / 是否分隔线。</summary>
/// <param name="Id">条目 id（<see cref="TrayMenuActions"/> 的常量；分隔线为空串）。</param>
/// <param name="Label">菜单文案（取自 <see cref="UiCopy"/>；分隔线为空串）。</param>
/// <param name="Separator">是否分隔线。</param>
public readonly record struct TrayMenuEntry(string Id, string Label, bool Separator);

/// <summary>
/// 托盘菜单构造（纯函数，可单测）：菜单条目是本地模型，Avalonia 原生菜单只按条目 id 映射动作
/// （ADR 2026-10-23-avalonia-g3-exit-restart）：菜单动作是原生回调，不经页面事件中继。
/// </summary>
public static class TrayMenuActions
{
    /// <summary>菜单：显示主窗。</summary>
    public const string ShowItemId = "show";

    /// <summary>菜单：检查更新（仅自更新栈装载时出现）。</summary>
    public const string CheckUpdateItemId = "check-update";

    /// <summary>菜单：重启应用。</summary>
    public const string RestartItemId = "restart";

    /// <summary>菜单：退出。</summary>
    public const string QuitItemId = "quit";

    /// <summary>构造托盘菜单条目（本地模型）：显示主窗 / 检查更新（可选）/ 分隔线 / 重启 / 退出。
    /// 内容（文案与位次）是单一家，宿主适配到自己的菜单类型（Avalonia <c>NativeMenu</c>）。</summary>
    /// <param name="includeUpdateItem">自更新栈是否装载（决定「检查更新」项是否出现）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省取 <see cref="UiLocale.DefaultLanguage"/>）——随 dsh
    /// 语言切换由 <c>UiLocale.Changed</c> 订阅方重建菜单（ADR host-ui-locale）；语言判定单一事实源在
    /// <see cref="UiLanguageResolver"/>（八语言分支，未注册 locale 回落英文）。</param>
    /// <returns>菜单条目序列（含分隔线条目）。</returns>
    public static List<TrayMenuEntry> BuildEntries(bool includeUpdateItem, UiLocale? uiLocale = null)
    {
        UiLanguage lang = uiLocale?.Language ?? UiLocale.DefaultLanguage;
        var items = new List<TrayMenuEntry>
        {
            new(ShowItemId, UiCopy.TrayShowMainWindow(lang), Separator: false),
        };
        if (includeUpdateItem)
        {
            items.Add(new(CheckUpdateItemId, UiCopy.TrayCheckForUpdates(lang), Separator: false));
        }

        items.Add(new(string.Empty, string.Empty, Separator: true));
        items.Add(new(RestartItemId, UiCopy.TrayRestart(lang), Separator: false));
        items.Add(new(QuitItemId, UiCopy.TrayQuit(lang), Separator: false));
        return items;
    }
}
