using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>profile package.json 的插件就位检测（纯判定 + 文件读取，Core 侧单点）：
/// 供随包/预设插件清单与安装驱动共用；检测失败按「未安装」处理（fail-safe，不阻断启动链）。</summary>
public static class ProfilePackageCheck
{
    /// <summary>精确检测插件是否已就位：<c>dependencies.&lt;pkg&gt;</c> 存在且 <c>dsh.profile.bundles</c> 含 <c>&lt;pkg&gt;</c>。</summary>
    public static bool IsBundleInstalled(string profilePkg, string packageName)
    {
        try
        {
            if (!File.Exists(profilePkg))
            {
                return false;
            }

            var root = JsonNode.Parse(File.ReadAllText(profilePkg));
            return root?["dependencies"] is JsonObject deps &&
                deps.ContainsKey(packageName) &&
                BundlesContain(root, packageName);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // 检测失败按「未安装」处理（fail-safe，不阻断启动链）：文件损坏/不可读/结构意外
            return false;
        }
    }

    /// <summary>构造 bundles 数组条目与包名的等值谓词（非字符串条目视为不等）。</summary>
    private static Func<JsonNode?, bool> BundleEntryEquals(string packageName) =>
        b => b is JsonValue v && v.TryGetValue<string>(out string? s) && s == packageName;

    /// <summary><c>dsh.profile.bundles</c> 是否已含 <paramref name="packageName"/>；结构缺失视为不含。</summary>
    private static bool BundlesContain(JsonNode root, string packageName)
    {
        if (root["dsh"]?["profile"]?["bundles"] is not JsonArray bundles)
        {
            return false;
        }

        return bundles.Any(BundleEntryEquals(packageName));
    }
}
