using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>随包插件后台安装的纯逻辑（可单测）：检测、workspace 修正、spec 解析、bundles 补写。
/// 本 partial 承载 JSON/profile 文件维护辅助；安装驱动见 <c>MarketInstallHelper.cs</c>。</summary>
public static partial class MarketInstallHelper
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
                !app.TryGetValue<string>(out string? appSpec) ||
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

            string text = File.ReadAllText(workspacePath);
            string original = text;
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

            string[] required = new[] { "@deepseek-ai/dsh-subprocess-local", "@google/genai", "koffi", "node-pty", "protobufjs" };
            foreach (string? pkg in required)
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

    /// <summary>解析桌面伴生插件的安装 <c>spec</c>：安装器资源 tgz（&gt;1K 防假包）；
    /// 无 registry 分发面，运行时目录种子随 online-first 退役（ADR online-first-unbundled-runtime 批次三）。</summary>
    /// <param name="installerPluginsDir">安装器自带的 resources/plugins（打包形态唯一供给源）。</param>
    /// <returns>可安装的 spec；<see langword="null"/> 表示无任何来源（如开发用 PATH dsh），调用方跳过。</returns>
    public static string? ResolveCompanionSpec(string? installerPluginsDir)
    {
        if (!string.IsNullOrWhiteSpace(installerPluginsDir))
        {
            string packaged = Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz");
            if (IsUsableTgz(packaged, minBytes: 1024))
            {
                return packaged;
            }
        }

        return null;
    }

    /// <summary>tgz 可用性判定：存在且体积达下限（防 0.1.10 式假包）。</summary>
    private static bool IsUsableTgz(string path, int minBytes)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            return new FileInfo(path).Length > minBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // tgz 状态探测失败（恰被清理/无权限）：按「tgz 不可用」继续走后续回退
            return false;
        }
    }

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

            JsonNode? profile = root["dsh"]?["profile"];
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

    /// <summary>按缩进 JSON + 尾部换行原子写回 profile <c>package.json</c>（同目录临时文件 + 原子替换，
    /// 与 dsh 自身写盘格式一致；写入中途崩溃不产生半写状态）。</summary>
    internal static Task WriteProfilePkgAsync(string profilePkg, JsonNode root)
    {
        AtomicWriteFile(profilePkg, RenderProfileJson(root));
        return Task.CompletedTask;
    }

    /// <summary>按缩进 JSON + 尾部换行原子写回 profile <c>package.json</c>（同步版，供启动前 reconcile 使用）。</summary>
    internal static void WriteProfilePkg(string profilePkg, JsonNode root)
    {
        AtomicWriteFile(profilePkg, RenderProfileJson(root));
    }

    /// <summary>同目录临时文件 + 原子替换写文本（失败清理临时文件；目标不可写时抛，由调用方决定 fail-safe）。</summary>
    private static void AtomicWriteFile(string path, string text)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* 临时文件清理失败不影响结果 */ }
            throw;
        }
    }

    /// <summary>把 profile <c>package.json</c> 序列化为缩进 JSON + 尾部换行（与 dsh 自身写盘格式一致）。</summary>
    private static string RenderProfileJson(JsonNode root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray()) + "\n";
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
