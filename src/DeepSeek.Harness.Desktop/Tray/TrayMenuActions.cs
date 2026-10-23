using System.Text.Json;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Tray;

/// <summary>托盘交互动作（图标点击 / 菜单项的宿主语义）。</summary>
public enum TrayAction
{
    /// <summary>显示并召回主窗。</summary>
    ShowMainWindow,

    /// <summary>触发一次自更新检查。</summary>
    CheckUpdate,

    /// <summary>重启应用（回收运行时后拉起新实例）。</summary>
    Restart,

    /// <summary>真正退出应用（放行关窗）。</summary>
    Quit,
}

/// <summary>托盘菜单条目的本地模型（与托盘框架无关）：id / 文案 / 是否分隔线。</summary>
/// <param name="Id">条目 id（<see cref="TrayMenuActions"/> 的常量；分隔线为空串）。</param>
/// <param name="Label">菜单文案（取自 <see cref="UiCopy"/>；分隔线为空串）。</param>
/// <param name="Separator">是否分隔线。</param>
public readonly record struct TrayMenuEntry(string Id, string Label, bool Separator);

/// <summary>
/// 托盘菜单构造与事件解析（纯函数，可单测）。事件名与载荷形状由 Ryn.Plugins.Tray 钉死：
/// <c>tray.clicked</c> 载荷为 null；<c>tray.menuItemClicked</c> 原生载荷是条目 id 的 JSON 字符串，
/// 经 Web 层中继（<c>window.__ryn.on</c> 回调）到达时已是解码后的 id 明文。
/// </summary>
public static class TrayMenuActions
{
    /// <summary>Ryn 托盘插件固定发出的图标点击事件名。</summary>
    public const string IconClickedEvent = "tray.clicked";

    /// <summary>Ryn 托盘插件固定发出的菜单项点击事件名。</summary>
    public const string MenuItemClickedEvent = "tray.menuItemClicked";

    /// <summary>菜单：显示主窗。</summary>
    public const string ShowItemId = "show";

    /// <summary>菜单：检查更新（仅自更新栈装载时出现）。</summary>
    public const string CheckUpdateItemId = "check-update";

    /// <summary>菜单：重启应用。</summary>
    public const string RestartItemId = "restart";

    /// <summary>菜单：退出。</summary>
    public const string QuitItemId = "quit";

    /// <summary>构造托盘菜单条目（与托盘框架无关的本地模型）：显示主窗 / 检查更新（可选）/ 分隔线 / 重启 / 退出。
    /// 两宿主共用这份内容，各自适配到自己的菜单类型（Ryn <see cref="TrayMenuItem"/> / Avalonia <c>NativeMenu</c>）。</summary>
    /// <param name="includeUpdateItem">自更新栈是否装载（决定「检查更新」项是否出现）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文）——随 dsh 语言切换由 <c>UiLocale.Changed</c>
    /// 订阅方重建菜单（ADR host-ui-locale）；非 <c>en*</c> 一律中文（对齐 dsh 字典兜底方向）。</param>
    /// <returns>菜单条目序列（含分隔线条目）。</returns>
    public static List<TrayMenuEntry> BuildEntries(bool includeUpdateItem, UiLocale? uiLocale = null)
    {
        bool english = uiLocale?.IsEnglish == true;
        var items = new List<TrayMenuEntry>
        {
            new(ShowItemId, UiCopy.TrayShowMainWindow(english), Separator: false),
        };
        if (includeUpdateItem)
        {
            items.Add(new(CheckUpdateItemId, UiCopy.TrayCheckForUpdates(english), Separator: false));
        }

        items.Add(new(string.Empty, string.Empty, Separator: true));
        items.Add(new(RestartItemId, UiCopy.TrayRestart(english), Separator: false));
        items.Add(new(QuitItemId, UiCopy.TrayQuit(english), Separator: false));
        return items;
    }

    /// <summary>旧宿主（Ryn）菜单条目：由 <see cref="BuildEntries"/> 的本地模型适配。</summary>
    /// <param name="includeUpdateItem">自更新栈是否装载。</param>
    /// <param name="uiLocale">UI 语言单点（可选）。</param>
    /// <returns>Ryn 托盘菜单条目。</returns>
    public static List<TrayMenuItem> BuildItems(bool includeUpdateItem, UiLocale? uiLocale = null) =>
        BuildEntries(includeUpdateItem, uiLocale)
            .Select(entry => new TrayMenuItem { Id = entry.Id, Label = entry.Label, Separator = entry.Separator })
            .ToList();

    /// <summary>
    /// 把中继回宿主的托盘事件解析为动作。非托盘事件、未知条目、坏载荷一律返回 null（忽略），
    /// 不视为错误——Web 层可能出现任意未来事件名。
    /// </summary>
    /// <param name="eventName">托盘事件名（如 <c>tray.clicked</c>）。</param>
    /// <param name="data">事件载荷：图标点击为 null；菜单项为中继侧收到的 id 明文。</param>
    public static TrayAction? TryResolve(string? eventName, string? data)
    {
        if (string.Equals(eventName, IconClickedEvent, StringComparison.Ordinal))
        {
            return TrayAction.ShowMainWindow;
        }

        if (!string.Equals(eventName, MenuItemClickedEvent, StringComparison.Ordinal))
        {
            return null;
        }

        string? id = data?.Trim();
        // 容忍 JSON 字符串形态的载荷（原生直传形态 `"show"`），与中继明文形态等效
        if (id is ['"', .., '"'])
        {
            try
            {
                using var doc = JsonDocument.Parse(id);
                if (doc.RootElement.ValueKind == JsonValueKind.String)
                {
                    id = doc.RootElement.GetString();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return id switch
        {
            ShowItemId => TrayAction.ShowMainWindow,
            CheckUpdateItemId => TrayAction.CheckUpdate,
            RestartItemId => TrayAction.Restart,
            QuitItemId => TrayAction.Quit,
            _ => null,
        };
    }
}
