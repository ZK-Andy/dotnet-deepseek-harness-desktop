using System.Globalization;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// <c>window.__shell</c> 信封协议（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：
/// 上行 <c>{"v":&lt;<see cref="Version"/>&gt;,"n":&lt;nonce&gt;,"id":&lt;int&gt;,"cmd":"&lt;id&gt;","args":&lt;json|null&gt;}</c>，
/// 下行 <c>__shell._resolve(id, ok, payload)</c>，事件 <c>__shell._emit(name, payload)</c>。
/// 解析方向走 <see cref="JsonDocument"/>（与 <c>AppJson</c> 的桥接回包约定一致）；脚本拼装是唯一拼点，
/// 载荷只接受合法 JSON（非法即按 JSON 字符串再编码，绝不把任意文本拼进 JS 表达式）。
/// </summary>
internal static class ShellBridgeProtocol
{
    /// <summary>信封版本（页面侧脚本从本常量插值，无第二家）。</summary>
    public const int Version = 1;

    /// <summary>解析上行消息；任一字段时间不成立即整体拒绝（不抛异常）。</summary>
    /// <param name="body">消息原文。</param>
    /// <param name="nonce">当前文档 nonce。</param>
    /// <param name="id">请求 id。</param>
    /// <param name="command">命令 ID。</param>
    /// <param name="argsJson">参数帧 JSON 原文（缺省 "null"）。</param>
    /// <returns>信封合法且 nonce 匹配时为 true。</returns>
    public static bool TryParse(string? body, string nonce, out long id, out string command, out string argsJson)
    {
        id = 0;
        command = string.Empty;
        argsJson = "null";
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("v", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int versionValue) ||
                versionValue != Version)
            {
                return false;
            }

            if (!root.TryGetProperty("n", out JsonElement token) ||
                token.ValueKind != JsonValueKind.String ||
                !string.Equals(token.GetString(), nonce, StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("id", out JsonElement idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                !idElement.TryGetInt64(out id))
            {
                return false;
            }

            if (!root.TryGetProperty("cmd", out JsonElement commandElement) ||
                commandElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(commandElement.GetString()))
            {
                return false;
            }

            command = commandElement.GetString()!;
            argsJson = root.TryGetProperty("args", out JsonElement args) &&
                       args.ValueKind is not JsonValueKind.Undefined
                ? args.GetRawText()
                : "null";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>回包脚本：<c>__shell._resolve(id, ok, payload)</c>。</summary>
    /// <param name="id">请求 id。</param>
    /// <param name="ok">成功/失败。</param>
    /// <param name="payloadJson">载荷（合法 JSON 表达式；非法时按字符串编码）。</param>
    /// <returns>可注入的脚本。</returns>
    public static string ResolveScript(long id, bool ok, string? payloadJson) =>
        "(function(){var s=window.__shell;if(s&&s._resolve){s._resolve("
        + id.ToString(CultureInfo.InvariantCulture)
        + (ok ? ",true," : ",false,")
        + JsValue(payloadJson)
        + ");}})()";

    /// <summary>事件脚本：<c>__shell._emit(name, payload)</c>。</summary>
    /// <param name="name">事件名。</param>
    /// <param name="payloadJson">载荷（合法 JSON 表达式；非法时按字符串编码）。</param>
    /// <returns>可注入的脚本。</returns>
    public static string EmitScript(string name, string? payloadJson) =>
        "(function(){var s=window.__shell;if(s&&s._emit){s._emit("
        + AppJsonContext.JsString(name)
        + ","
        + JsValue(payloadJson)
        + ");}})()";

    /// <summary>载荷 → JS 表达式：合法 JSON 原样内嵌，否则按 JSON 字符串编码（不拼裸文本）。</summary>
    /// <param name="payloadJson">载荷原文。</param>
    /// <returns>JS 表达式。</returns>
    private static string JsValue(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "null";
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return payloadJson;
        }
        catch (JsonException)
        {
            return AppJsonContext.JsString(payloadJson);
        }
    }
}
