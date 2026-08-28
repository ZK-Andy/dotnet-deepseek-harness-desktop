using System.Text.Json;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>托盘交互动作（图标点击 / 菜单项的宿主语义）。</summary>
public enum TrayAction
{
    /// <summary>显示并召回主窗。</summary>
    ShowMainWindow,

    /// <summary>触发一次自更新检查。</summary>
    CheckUpdate,

    /// <summary>真正退出应用（放行关窗）。</summary>
    Quit,
}

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

    /// <summary>菜单：退出。</summary>
    public const string QuitItemId = "quit";

    /// <summary>构造托盘菜单：显示主窗 / 检查更新（可选）/ 分隔线 / 退出。</summary>
    /// <param name="includeUpdateItem">自更新栈是否装载（决定「检查更新」项是否出现）。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文）——随 dsh 语言切换由 <c>UiLocale.Changed</c>
    /// 订阅方重建菜单（ADR host-ui-locale）；非 <c>en*</c> 一律中文（对齐 dsh 字典兜底方向）。</param>
    public static List<TrayMenuItem> BuildItems(bool includeUpdateItem, UiLocale? uiLocale = null)
    {
        var english = uiLocale?.IsEnglish == true;
        var items = new List<TrayMenuItem>
        {
            new() { Id = ShowItemId, Label = english ? "Show Main Window" : "显示主窗" },
        };
        if (includeUpdateItem)
        {
            items.Add(new() { Id = CheckUpdateItemId, Label = english ? "Check for Updates" : "检查更新" });
        }

        items.Add(new() { Id = "", Label = "", Separator = true });
        items.Add(new() { Id = QuitItemId, Label = english ? "Quit" : "退出" });
        return items;
    }

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

        var id = data?.Trim();
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
            QuitItemId => TrayAction.Quit,
            _ => null,
        };
    }
}
