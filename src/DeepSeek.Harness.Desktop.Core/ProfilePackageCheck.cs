using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// profile package.json 的插件就位检测（纯判定 + 文本解析，Core 侧单点）：
/// 供随包/预设插件清单与安装驱动共用；检测失败按「未安装」处理（fail-safe，不阻断启动链）。
/// 文本从哪来由调用方给（读盘在 Infrastructure 的 <see cref="Plugins.IPluginPackageReader"/> 实现）。
/// </summary>
public static class ProfilePackageCheck
{
    /// <summary>精确检测插件是否已就位：<c>dependencies.&lt;pkg&gt;</c> 存在且 <c>dsh.profile.bundles</c> 含 <c>&lt;pkg&gt;</c>。</summary>
    /// <param name="profilePackageJson">profile package.json 文本；null（文件缺失/读不出）按未安装处理。</param>
    /// <param name="packageName">包名。</param>
    /// <returns>已就位为 true。</returns>
    public static bool IsBundleInstalled(string? profilePackageJson, string packageName)
    {
        if (profilePackageJson is null)
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(profilePackageJson);
            return root?["dependencies"] is JsonObject deps &&
                deps.ContainsKey(packageName) &&
                BundlesContain(root, packageName);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 检测失败按「未安装」处理（fail-safe，不阻断启动链）：文本损坏/结构意外
            return false;
        }
    }

    /// <summary>bundles 数组条目与包名的等值谓词（非字符串条目视为不等）。</summary>
    /// <param name="entry">bundles 数组的一个条目。</param>
    /// <param name="packageName">包名。</param>
    /// <returns>条目即该包名为 true。</returns>
    public static bool BundleEntryEquals(JsonNode? entry, string packageName) =>
        entry is JsonValue v && v.TryGetValue<string>(out string? s) && s == packageName;

    /// <summary><c>dsh.profile.bundles</c> 是否已含 <paramref name="packageName"/>；结构缺失视为不含。</summary>
    private static bool BundlesContain(JsonNode root, string packageName)
    {
        if (root["dsh"]?["profile"]?["bundles"] is not JsonArray bundles)
        {
            return false;
        }

        return bundles.Any(entry => BundleEntryEquals(entry, packageName));
    }
}
