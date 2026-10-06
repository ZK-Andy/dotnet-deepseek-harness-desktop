using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// appsettings.json 节装卸共享缝（解析半边；读文件的 fail-safe 半边单源在 Infrastructure 侧
/// <c>ConfigSectionFile</c>——文件 IO 不进 Core，D005 边界纪律）：<c>RuntimeTimeouts</c>/<c>UpdateOptions</c>/<c>ProxyLogging</c>
/// 三份选项记录同形的「全文解析 → 抽节 → 逐键容错覆盖」逻辑单源——根/节形态防御与数值/布尔键容错只写一个家。
/// </summary>
internal static class ConfigSectionJson
{
    /// <summary>解析全文并抽取 <paramref name="sectionName"/> 节：节存在且为对象时以节元素调
    /// <paramref name="parseSection"/>；根非对象、无节或节非对象一律返回 <paramref name="fallback"/>，
    /// 绝不把形态损坏升级成异常（根非对象时 <c>TryGetProperty</c> 会抛 <see cref="InvalidOperationException"/>，
    /// 启动早期调用即启动崩）。损坏 JSON 的 <see cref="JsonException"/> 上抛——fail loud 与 fail-safe
    /// 的分界在 IO 边界，由调用方 Load 转 fail-safe。</summary>
    /// <typeparam name="T">选项记录类型。</typeparam>
    /// <param name="json">appsettings.json 全文。</param>
    /// <param name="sectionName">节名（与选项类型的 appsettings 键一致）。</param>
    /// <param name="parseSection">节内逐键覆盖（在 <see cref="JsonDocument"/> 存活窗口内调用——
    /// <see cref="JsonElement"/> 是 doc 的视图，回调外即悬垂，生命周期由本缝统一兜住）。</param>
    /// <param name="fallback">节缺失/形态不符时返回的默认实例。</param>
    /// <returns>解析后的选项。</returns>
    public static T Parse<T>(string json, string sectionName, Func<JsonElement, T> parseSection, T fallback)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty(sectionName, out JsonElement section) ||
            section.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        return parseSection(section);
    }

    /// <summary>数值键读取：非数值或不可表示为 Int32（小数/越界——<c>GetInt32</c> 会抛
    /// <see cref="FormatException"/>）或小于 <paramref name="floor"/> 一律回退默认值，
    /// 绝不把配置损坏升级成启动异常。</summary>
    /// <param name="section">节元素。</param>
    /// <param name="name">键名（与选项属性名一致）。</param>
    /// <param name="current">该键的默认值（回退目标）。</param>
    /// <param name="floor">取值下界（默认 <see cref="int.MinValue"/> 即无钳制，与既有键同哲学）。</param>
    /// <returns>解析值或默认值。</returns>
    public static int GetInt(JsonElement section, string name, int current, int floor = int.MinValue) =>
        section.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed) &&
        parsed >= floor
            ? parsed
            : current;

    /// <summary>布尔键读取：非 <c>true</c>/<c>false</c> 字面量一律回退默认值。</summary>
    /// <param name="section">节元素。</param>
    /// <param name="name">键名（与选项属性名一致）。</param>
    /// <param name="current">该键的默认值（回退目标）。</param>
    /// <returns>解析值或默认值。</returns>
    public static bool GetBool(JsonElement section, string name, bool current) =>
        section.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : current;
}
