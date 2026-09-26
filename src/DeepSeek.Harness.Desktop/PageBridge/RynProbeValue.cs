using System.Text.Json;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// Ryn 桥返回值解码（探针共用）：<c>EvaluateJavaScriptAsync</c> 的回报是 JSON 文档，页面返回字符串时
/// 到这边是**带引号与转义的 JSON 字符串字面量**——直接当文本用会把外壳算进字段
/// （仓内先例 <c>PageHealthMonitor.Parse</c>：夹具兼收 <c>text:42</c> 与 <c>"text:42"</c>，生产码 <c>Trim('"')</c>）。
/// 解析方向走 <see cref="JsonDocument"/>（<c>AppJson</c> 的同一约定：桥接回包有再序列化怪癖，
/// 不用反射式序列化通道）：字符串 → 还原转义后的文本，非字符串/不可解析 → 原样返回
/// （不认识的形态交上层判未知，绝不猜）。
/// </summary>
internal static class RynProbeValue
{
    /// <summary>解码桥回报；null/空 → null。</summary>
    /// <param name="raw">桥回报原文。</param>
    /// <returns>原始文本（JSON 字符串已还原转义）；非字符串或无法解析时原样返回。</returns>
    public static string? Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            // 字符串 → 还原转义；JSON null（探针无值）→ null；数字/对象等非字符串形态原样交上层判。
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Null => null,
                _ => raw,
            };
        }
        catch (JsonException)
        {
            // 非 JSON（裸值形态）：原样交给裁决层，由其按"可拆/不可拆"判未知。
            return raw;
        }
    }
}
