using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Core.Plugins;

/// <summary>
/// 随包插件版本判定的纯逻辑（ADR core-purity-boundaries）：只做 package.json 文本的版本解析与比较，
/// **不读盘**——读盘在 Infrastructure 的 <see cref="IPluginPackageReader"/> 实现。
/// </summary>
/// <remarks>随包侧（tgz/闭包目录）结构异常 fail loud 抛出——那是我们自己的产物，坏了必须可见；
/// 已装侧（profile node_modules）读不出/损坏按「未知」处理（返回 null），由调用方走重装修复。
/// 版本比较复用 <see cref="Update.UpdateVersion"/>（数字段逐段比）。</remarks>
public static class PluginVersionCheck
{
    /// <summary>从 package.json 文本取版本号。</summary>
    /// <param name="packageJson">package.json 文本。</param>
    /// <returns>版本号；缺失/非字符串/空串返回 null（未知）。</returns>
    private static string? TryExtractVersion(string packageJson)
    {
        using var doc = JsonDocument.Parse(packageJson);
        if (!doc.RootElement.TryGetProperty("version", out JsonElement v) ||
            v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? version = v.GetString();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    /// <summary>从 package.json 文本取版本号；缺失或非法即抛（随包产物坏了必须可见）。</summary>
    /// <param name="packageJson">package.json 文本。</param>
    /// <returns>版本号。</returns>
    /// <exception cref="InvalidDataException">package.json 缺少有效的 version 字段。</exception>
    /// <exception cref="System.Text.Json.JsonException">package.json 文本不是合法 JSON。</exception>
    public static string ReadVersion(string packageJson) =>
        TryExtractVersion(packageJson)
            ?? throw new InvalidDataException("package.json 缺少有效的 version 字段");

    /// <summary>从 package.json 文本取版本号；文本损坏或字段缺失一律按未知（null）——用于「已装副本」侧
    /// （profile 是可再生状态，读不出按未知走重装修复，不当故障）。</summary>
    /// <param name="packageJson">package.json 文本。</param>
    /// <returns>版本号；未知为 null。</returns>
    public static string? TryReadVersion(string packageJson)
    {
        try
        {
            return TryExtractVersion(packageJson);
        }
        catch (JsonException)
        {
            // 文本损坏 = 版本未知（不是包坏了）——调用方据此走升级/重装
            return null;
        }
    }

    /// <summary>是否需要安装/升级：已装版本不可读（含未装），或随包版本更新（<see cref="Update.UpdateVersion.Compare"/> &gt; 0）。</summary>
    /// <exception cref="ArgumentException">任一版本号无法逐段解析（fail loud，由调用方转日志跳过）。</exception>
    public static bool NeedsUpgrade(string? installedVersion, string bundledVersion)
    {
        return installedVersion is null || Update.UpdateVersion.Compare(bundledVersion, installedVersion) > 0;
    }
}
