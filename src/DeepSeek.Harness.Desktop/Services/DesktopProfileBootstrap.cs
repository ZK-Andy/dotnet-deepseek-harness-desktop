using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 桌面专属 profile 自举（ADR shared-home-desktop-profile）。上游 app-boot 只对内置模板
/// （web/headless）自动初始化 profile；自定义名（desktop）在 <c>profiles/&lt;name&gt;/package.json</c>
/// 缺失时直接拒启。因此壳在首次 spawn 前按上游 <c>initProfile</c>（dsh-app-boot）同款三件套
/// 自举：<c>package.json</c>（bundles 对齐 web 模板——缺 <c>dsh-web-app</c> 则永远出不了
/// <c>dsh web:</c> URL）+ 空 <c>cordis.patch.yml</c> + <c>pnpm-workspace.yaml</c>。
/// 幂等且永不覆写已存在文件：profile 一经初始化（含用户手工管理），所有权归 dsh/用户。
/// </summary>
public static class DesktopProfileBootstrap
{
    /// <summary>初始 bundles：对齐上游 web 模板（base + web-app），桌面才有 Web UI 可加载；随包插件由安装任务追加。</summary>
    internal static readonly string[] InitialBundles =
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
    };

    /// <summary>与上游 dsh-app-boot 的 PROFILE_PATCH_TEMPLATE 逐字一致。</summary>
    internal const string PatchTemplate =
        "# Your patch layer for this dsh profile, applied after every bundle layer:\n" +
        "# a top-level YAML array of loader patch entries (id-targeted config\n" +
        "# overrides, disables, and insert lists; `!!js` expressions allowed).\n" +
        "[]\n";

    /// <summary>与上游 dsh-app-boot 的 PROFILE_PNPM_WORKSPACE 逐字一致。</summary>
    internal const string PnpmWorkspaceTemplate =
        "packages:\n" +
        "  - .\n" +
        "\n" +
        "nodeLinker: hoisted\n" +
        "autoInstallPeers: false\n";

    /// <summary>
    /// 确保 desktop profile 就绪（幂等）。缺失的文件逐个补齐、已存在的一律不碰。
    /// </summary>
    /// <param name="dshHome">共享 DSH_HOME 绝对路径。</param>
    /// <returns>本次是否新写了 profile 清单（package.json）——仅用于启动日志。</returns>
    public static bool EnsureProfile(string dshHome)
    {
        var dir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(dir);

        var manifestPath = Path.Combine(dir, "package.json");
        var createdManifest = false;
        if (!File.Exists(manifestPath))
        {
            // 形态对齐上游 initProfile：private 工作区清单 + 有序 bundles 列表。
            // 手拼 JSON（字段全为自有常量，无转义面；缩进排版须与上游模板逐字对齐，紧凑源生成给不了）
            var bundles = string.Join(", ", InitialBundles.Select(b => "\"" + b + "\""));
            var json =
                "{\n" +
                "  \"name\": \"dsh-profile-" + HarnessRuntimeHost.DesktopProfileName + "\",\n" +
                "  \"private\": true,\n" +
                "  \"dependencies\": {},\n" +
                "  \"dsh\": {\n" +
                "    \"profile\": {\n" +
                "      \"bundles\": [" + bundles + "]\n" +
                "    }\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(manifestPath, json);
            createdManifest = true;
        }

        var patchPath = Path.Combine(dir, "cordis.patch.yml");
        if (!File.Exists(patchPath))
        {
            File.WriteAllText(patchPath, PatchTemplate);
        }

        var workspacePath = Path.Combine(dir, "pnpm-workspace.yaml");
        if (!File.Exists(workspacePath))
        {
            File.WriteAllText(workspacePath, PnpmWorkspaceTemplate);
        }

        return createdManifest;
    }

    /// <summary>
    /// 启动前 reconcile 不可解析的 bundle 引用（ADR online-first-unbundled-runtime 批次三，
    /// 对齐 dsh-tauri-desk #177：壳升级后 dsh 配置仍引用已消失的插件包 → 启动卡死循环）。
    /// 扫描 desktop profile 的 <c>dependencies</c>，凡声明为本地 <c>file:</c>/<c>link:</c> 形态而
    /// 其路径目标已不存在（被退役的随包种子属之）的，从 <c>dependencies</c> 与
    /// <c>dsh.profile.bundles</c> 一并移除。幂等；结构损坏/不可读按 fail-safe 不碰并记日志。
    /// </summary>
    /// <param name="dshHome">共享 DSH_HOME 绝对路径。</param>
    /// <param name="log">诊断日志出口（host.log 同款行文）。</param>
    /// <returns>本次移除的不可解析引用条目数。</returns>
    public static int ReconcileProfile(string dshHome, Action<string> log)
    {
        var dir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        var manifestPath = Path.Combine(dir, "package.json");
        if (!File.Exists(manifestPath))
        {
            return 0;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 清单不可读：无法安全改写，按 fail-safe 不碰（不阻断启动链，只记日志留痕）
            log($"[host] 桌面 profile 清单 reconcile 失败（不可读）：{ex.Message}");
            return 0;
        }

        if (root?["dependencies"] is not JsonObject deps)
        {
            return 0;
        }

        var removable = new List<(string Name, string Spec)>();
        foreach (var (name, value) in deps)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var spec) && IsDeadLocalPath(spec, dir))
            {
                removable.Add((name, spec));
            }
        }

        if (removable.Count == 0)
        {
            return 0;
        }

        foreach (var (name, _) in removable)
        {
            deps.Remove(name);
        }

        // bundles 若存在但非数组（损坏态）：整体放弃移除，避免写回「dependencies 已删、bundles 残留」的
        // 半 reconcile 状态——半写会把不可解析引用留在 bundles，反而违背 reconcile 的初衷。
        var profileNode = root["dsh"]?["profile"];
        if (profileNode is JsonObject profile && profile["bundles"] is not null && profile["bundles"] is not JsonArray)
        {
            log($"[host] 桌面 profile reconcile 放弃（bundles 结构损坏，非数组）：{manifestPath}");
            return 0;
        }

        if (root["dsh"]?["profile"]?["bundles"] is JsonArray bundles)
        {
            foreach (var (name, _) in removable)
            {
                var matches = bundles.Where(b =>
                    b is JsonValue v && v.TryGetValue<string>(out var s) && s == name).ToList();
                foreach (var m in matches)
                {
                    bundles.Remove(m);
                }
            }
        }

        MarketInstallHelper.WriteProfilePkg(manifestPath, root);
        foreach (var (name, spec) in removable)
        {
            log($"[host] 桌面 profile reconcile：移除不可解析插件引用 {name}（{spec}）");
        }

        return removable.Count;
    }

    /// <summary>spec 是否为本地 <c>file:</c>/<c>link:</c> 形态且其路径目标已不存在（参数
    /// <paramref name="profileDir"/> 用于解析相对路径）。registry/别名/github 等非本地形态返回 false。</summary>
    private static bool IsDeadLocalPath(string spec, string profileDir)
    {
        string? target = null;
        if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            target = spec["file:".Length..];
        }
        else if (spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
        {
            target = spec["link:".Length..];
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var full = Path.IsPathRooted(target)
            ? target
            : Path.Combine(profileDir, target);
        return !File.Exists(full) && !Directory.Exists(full);
    }
}
