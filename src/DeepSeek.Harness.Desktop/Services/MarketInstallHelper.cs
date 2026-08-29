using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>随包插件后台安装的纯逻辑（可单测）：检测、迁移、workspace 修正、spec 解析、bundles 补写。</summary>
/// <remarks>随包插件当前有两项：<c>dshmarket</c>（市场，registry 有上游）与 <c>dsh-desktop-companion</c>
/// （桌面伴生，仅随包分发、无 registry 回退）。</remarks>
public static class MarketInstallHelper
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

    /// <summary>清理 <c>0.1.10</c> 误写入的 <c>dependencies.app=file:...dshmarket.tgz</c>。</summary>
    public static async Task CleanupBogusAppDependencyAsync(string profilePkg)
    {
        try
        {
            if (!File.Exists(profilePkg))
            {
                return;
            }

            var root = JsonNode.Parse(await File.ReadAllTextAsync(profilePkg));
            if (root?["dependencies"]?["app"] is not JsonValue app ||
                !app.TryGetValue<string>(out var appSpec) ||
                !appSpec.Contains("dshmarket.tgz", StringComparison.Ordinal))
            {
                return;
            }

            root["dependencies"]!.AsObject().Remove("app");
            await WriteProfilePkgAsync(profilePkg, root);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // 假依赖清理是尽力而为（fail-safe，不阻断启动链）：文件损坏/不可读/结构意外按「不清」处理
        }
    }

    /// <summary>确保 <c>pnpm-workspace.yaml</c> 的 <c>allowBuilds</c> 放行 6 项原生构建。</summary>
    public static void EnsureWorkspaceAllowBuilds(string workspacePath)
    {
        try
        {
            if (!File.Exists(workspacePath))
            {
                return;
            }

            var text = File.ReadAllText(workspacePath);
            var original = text;
            if (text.Contains("set this to true or false"))
            {
                text = text.Replace(": set this to true or false", ": true");
            }

            if (!text.Contains("esbuild"))
            {
                if (text.Contains("allowBuilds:"))
                {
                    text = text.Replace("allowBuilds:", "allowBuilds:\n  esbuild: true");
                }
                else
                {
                    text = text.TrimEnd() + "\nallowBuilds:\n  esbuild: true\n";
                }
            }
            else if (text.Contains("esbuild: set this"))
            {
                text = text.Replace("esbuild: set this to true or false", "esbuild: true");
            }

            var required = new[] { "@deepseek-ai/dsh-subprocess-local", "@google/genai", "koffi", "node-pty", "protobufjs" };
            foreach (var pkg in required)
            {
                if (!text.Contains(pkg))
                {
                    if (text.Contains("allowBuilds:"))
                    {
                        text = text.Replace("allowBuilds:", $"allowBuilds:\n  '{pkg}': true");
                    }
                }
            }

            if (text != original)
            {
                File.WriteAllText(workspacePath, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // workspace 修正是尽力而为（fail-safe，不阻断启动链）：文件不可读/不可写按「不修」处理
        }
    }

    /// <summary>解析市场安装的 <c>spec</c>：优先校验过的 <c>tgz &gt;10K</c>，否则目录或 registry。</summary>
    public static string ResolveMarketSpec(string runtimeDir)
    {
        var tgz = Path.Combine(runtimeDir, "dshmarket.tgz");
        if (File.Exists(tgz))
        {
            try
            {
                var fi = new FileInfo(tgz);
                if (fi.Length > 10 * 1024)
                {
                    return tgz;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // tgz 状态探测失败（恰被清理/无权限）：按「tgz 不可用」继续走目录/配置回退
            }
        }

        var dir = Path.Combine(runtimeDir, "node_modules", "dshmarket");
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "package.json")))
        {
            return dir;
        }

        return BundledPluginCatalog.MarketRegistryFallback;
    }

    /// <summary>解析桌面伴生插件的安装 <c>spec</c>：随包 <c>tgz</c>（&gt;1K 防假包）→ 闭包内目录；无 registry 回退。</summary>
    /// <returns>可安装的 spec；<see langword="null"/> 表示本闭包未携带（如开发用 PATH dsh），调用方跳过。</returns>
    public static string? ResolveCompanionSpec(string runtimeDir)
    {
        var tgz = Path.Combine(runtimeDir, "dsh-desktop-companion.tgz");
        if (File.Exists(tgz))
        {
            try
            {
                var fi = new FileInfo(tgz);
                if (fi.Length > 1024)
                {
                    return tgz;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // tgz 状态探测失败（恰被清理/无权限）：按「tgz 不可用」继续走目录回退
            }
        }

        var dir = Path.Combine(runtimeDir, "node_modules", "dsh-desktop-companion");
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "package.json")))
        {
            return dir;
        }

        return null;
    }

    /// <summary>读 profile <c>dependencies</c> 中 <paramref name="packageName"/> 的 spec 原始值。
    /// 未装、值非字符串或文件不可读返回 <see langword="null"/>（与 <see cref="IsBundleInstalled"/> 同款 fail-safe）。</summary>
    public static string? ReadDependencySpec(string profilePkg, string packageName)
    {
        try
        {
            if (!File.Exists(profilePkg))
            {
                return null;
            }

            var root = JsonNode.Parse(File.ReadAllText(profilePkg));
            return root?["dependencies"] is JsonObject deps &&
                deps[packageName] is JsonValue value &&
                value.TryGetValue<string>(out var spec)
                ? spec
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // 检测失败按「形态未知」处理（fail-safe，不阻断启动链）：文件损坏/不可读/结构意外
            return null;
        }
    }

    /// <summary>spec 是否为本地形态（<c>file:</c>/<c>link:</c> 前缀，大小写不敏感）——由本机路径安装、
    /// 非 registry 所有。registry 形态（semver/range/别名等）一律 <see langword="false"/>；null 视为非本地。
    /// 用于 profile dependencies 存量值的形态判定。</summary>
    public static bool IsLocalSpec(string? spec) =>
        spec is not null && (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase));

    /// <summary>spec 是否为本地路径形态（含路径分隔符，含 <c>file:</c>/<c>link:</c> 前缀）——
    /// 区别于裸包名与 registry 串。待装清单的 spec 来自解析器（绝对路径/目录）或归化（裸包名），
    /// 据此分组成 spawn 事务。</summary>
    public static bool IsPathSpec(string? spec) =>
        !string.IsNullOrEmpty(spec) && (spec.Contains('/') || spec.Contains('\\'));

    /// <summary>确保 <c>dsh.profile.bundles</c> 含 <paramref name="packageName"/>，缺则追加并写回。</summary>
    /// <returns>是否发生写回。</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "JsonArray.Add 的元素恒为 string（包名，基元类型），无非基元序列化的裁剪风险")]
    [UnconditionalSuppressMessage("Aot", "IL3050", Justification = "同上：JsonValue.Create(string) 为基元类型，无运行时代码生成")]
    public static async Task<bool> EnsureBundlesContainsAsync(string profilePkg, string packageName)
    {
        try
        {
            if (!File.Exists(profilePkg))
            {
                return false;
            }

            var root = JsonNode.Parse(await File.ReadAllTextAsync(profilePkg));
            if (root is null)
            {
                return false;
            }

            var profile = root["dsh"]?["profile"];
            switch (profile?["bundles"])
            {
                case JsonArray bundles:
                    if (bundles.Any(BundleEntryEquals(packageName)))
                    {
                        return false;
                    }

                    bundles.Add(packageName);
                    break;
                case not null:
                    // bundles 结构意外（非数组）：按损坏处理，不写回
                    return false;
                case null when profile is not null:
                    profile["bundles"] = new JsonArray(packageName);
                    break;
                // dsh/profile 缺失：不改结构，原样回写（保持既有语义：返回 true）
            }

            await WriteProfilePkgAsync(profilePkg, root);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // bundles 补写是尽力而为（fail-safe，不阻断启动链）：文件损坏/不可读/结构意外按「未补」处理
            return false;
        }
    }

    /// <summary>按缩进 JSON + 尾部换行写回 profile <c>package.json</c>（与 dsh 自身写盘格式一致）。</summary>
    private static async Task WriteProfilePkgAsync(string profilePkg, JsonNode root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }

        var newJson = System.Text.Encoding.UTF8.GetString(ms.ToArray()) + "\n";
        await File.WriteAllTextAsync(profilePkg, newJson);
    }

    /// <summary>构造 bundles 数组条目与包名的等值谓词（非字符串条目视为不等）。</summary>
    private static Func<JsonNode?, bool> BundleEntryEquals(string packageName) =>
        b => b is JsonValue v && v.TryGetValue<string>(out var s) && s == packageName;

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
