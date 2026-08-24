using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 托盘唤回的窗口态探测（纯函数可单测）：WebView 页面里 outerWidth/Height 与
/// screen.availWidth/Height 同为 CSS 像素，前者达到后者即视为最大化/全屏类大窗——
/// 这是 IRynWindow 缺少状态查询属性时唯一自持的查询通道（ADR tray-recall-maximize-and-check-feedback）。
/// </summary>
internal static class TrayWindowStateProbe
{
    /// <summary>容差：部分合成器在可用区边缘留边框像素，不超过此值仍算占满。</summary>
    private const int TolerancePx = 16;

    /// <summary>注入脚本：返回视口与可用工作区的尺寸快照（JSON 字符串）。</summary>
    public const string Script =
        "(function(){return JSON.stringify({w:window.outerWidth,h:window.outerHeight,sw:screen.availWidth,sh:screen.availHeight});})();";

    /// <summary>判定是否大窗（最大化或全屏）：宽高都达到可用区（含容差）。</summary>
    public static bool IsMaximized(int width, int height, int availWidth, int availHeight) =>
        width >= availWidth - TolerancePx && height >= availHeight - TolerancePx;

    /// <summary>
    /// 解析脚本回传并判定；输入可能是裸 JSON 或被桥接层引号包裹的字符串，
    /// 字段缺失/非法一律返回 null（未知 ≠ 非最大化）。
    /// </summary>
    public static bool? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.StartsWith('"'))
        {
            // 桥接层把返回值再序列化了一层：外层是 JSON 字符串节点（含 \u0022 类转义），
            // 用 JsonDocument 取 GetString 解码——纯解析无反射，PublishAot 安全
            try
            {
                using var wrapper = JsonDocument.Parse(text);
                if (wrapper.RootElement.ValueKind == JsonValueKind.String)
                {
                    text = wrapper.RootElement.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetInt(root, "w", out var w) || !TryGetInt(root, "h", out var h) ||
                !TryGetInt(root, "sw", out var sw) || !TryGetInt(root, "sh", out var sh))
            {
                return null;
            }

            return IsMaximized(w, h, sw, sh);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return prop.TryGetInt32(out value);
    }
}
