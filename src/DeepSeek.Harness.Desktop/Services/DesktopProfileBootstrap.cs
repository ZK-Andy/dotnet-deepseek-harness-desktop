using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 桌面专属 profile 自举（ADR shared-home-desktop-profile）。上游 app-boot 只对内置模板
/// （web/headless）自动初始化 profile；自定义名（<see cref="HarnessRuntimeHost.DesktopProfileName"/>）在
/// <c>profiles/&lt;name&gt;/package.json</c> 缺失时直接拒启。因此壳在首次 spawn 前按上游
/// <c>initProfile</c>（dsh-app-boot）同款三件套
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

    /// <summary>改名前的旧 profile 名（ADR desktop-profile-rename）：仅作迁移源，不再写入。</summary>
    internal const string LegacyProfileName = "desktop";

    /// <summary>官方 Electron 桌面端项目标识文件（ADR desktop-profile-rename）：<c>profiles/desktop</c>
    /// 在官方端安装后归其所有，这些文件由官方 package transaction / seed 复制与 host 启动写入。</summary>
    private static readonly string[] s_officialDesktopMarkers =
    {
        "desktop.cordis.yml",
        "desktop-packages.json",
        "desktop-release.json",
    };

    /// <summary>官方 Electron 端项目清单名（上游 <c>project-manager.ts</c> 的 <c>PROJECT_NAME</c>）：
    /// profile 一经官方端激活即写入，用作仅带清单的残留目录的兜底判据。</summary>
    internal const string OfficialProjectManifestName = "@deepseek-ai/dsh-desktop-runtime";

    /// <summary>
    /// 该 profile 目录是否归官方 Electron 桌面端所有（存在任一标识文件、已装官方 host 依赖，
    /// 或清单名即官方项目名）。判别只用在「不搬官方数据」的 fail-safe 一侧：识别不出官方特征即按我方存量迁移。
    /// </summary>
    /// <param name="profileDir">候选 profile 目录。</param>
    /// <returns>归官方端所有返回 true。</returns>
    internal static bool IsOfficialDesktopProject(string profileDir) =>
        s_officialDesktopMarkers.Any(marker => File.Exists(Path.Combine(profileDir, marker)))
        || Directory.Exists(Path.Combine(profileDir, "node_modules", "@deepseek-ai", "dsh-desktop-host"))
        || HasOfficialManifestName(profileDir);

    /// <summary>清单名是否为官方项目名：缺失/不可读/非法 JSON 一律判否——无法证明是官方项目时按我方存量迁移。</summary>
    private static bool HasOfficialManifestName(string profileDir)
    {
        string manifest = Path.Combine(profileDir, "package.json");
        if (!File.Exists(manifest))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(manifest))?["name"]?.GetValue<string>() == OfficialProjectManifestName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // 吞掉的是「读不到 / 解析不了 / name 非字符串」：三种都无法证明官方归属，
            // 一律按存量迁移（fail-safe 偏向不误伤我方用户，绝不因清单异常而放弃迁移）
            return false;
        }
    }

    /// <summary>
    /// 旧 profile 名一次性迁移（ADR desktop-profile-rename）：上游 dsh 0.1.5-alpha.1 起 CLI 把字面名
    /// <c>desktop</c> 圈占给官方 Electron 桌面端（无条件硬拒），壳的 profile 改名 <see cref="HarnessRuntimeHost.DesktopProfileName"/>，
    /// 存量 <c>profiles/desktop</c> 在 spawn 前改名迁移。规则：
    /// 新目录已存在 → no-op（绝不合并两目录，新目录所有权归 dsh/用户）；
    /// 旧目录归官方 Electron 端所有（<see cref="IsOfficialDesktopProject"/>）→ no-op，绝不搬官方数据，
    /// 由 <see cref="EnsureProfile"/> 自举全新 <c>dotnet-desktop</c>；
    /// 旧目录存在 → 同卷 <c>Directory.Move</c>（原子 rename，插件装配/端口记忆/PID 文件整体保留）；
    /// 移动失败 → 日志留痕、不抛出——降级为全新 profile 自举（<see cref="EnsureProfile"/> 兜底）；
    /// 旧目录保留，目标位让出前后续启动会继续尝试迁移并留痕，新目录一经自举成功即命中
    /// 「新目录已存在」跳过分支，绝不合并。
    /// </summary>
    /// <param name="dshHome">共享 DSH_HOME 绝对路径。</param>
    /// <param name="log">诊断日志出口（host.log 同款行文）。</param>
    public static void MigrateLegacyProfileName(string dshHome, Action<string> log)
    {
        string legacyDir = Path.Combine(dshHome, "profiles", LegacyProfileName);
        if (!Directory.Exists(legacyDir))
        {
            return;
        }

        string newDir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        if (Directory.Exists(newDir))
        {
            log($"[host] 桌面 profile 迁移跳过：{HarnessRuntimeHost.DesktopProfileName} 已存在（旧 {LegacyProfileName} 目录保留不合并）");
            return;
        }

        if (IsOfficialDesktopProject(legacyDir))
        {
            log($"[host] 桌面 profile 迁移跳过：profiles/{LegacyProfileName} 归官方 Electron 桌面端所有，不搬其数据（自举全新 profiles/{HarnessRuntimeHost.DesktopProfileName}）");
            return;
        }

        try
        {
            Directory.Move(legacyDir, newDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort 迁移位（同 PersistPort 契约）：失败不阻断启动，全新 profile 由 EnsureProfile
            // 自举兜底；旧目录保留——目标位让出前（如用户清掉占位文件）后续启动会继续尝试并留痕，
            // 新目录一经自举即命中「新目录已存在」跳过分支——绝不静默吞掉：日志给足人可判读信号
            log($"[host] 桌面 profile 迁移失败（将自举全新 profile，旧 {LegacyProfileName} 目录保留）：{ex.Message}");
            return;
        }

        // 成功日志在 try 之外（编码契约：try 只包一个语句）——日志写入失败不得被误报成「迁移失败」
        log($"[host] 桌面 profile 已迁移：profiles/{LegacyProfileName} → profiles/{HarnessRuntimeHost.DesktopProfileName}");
    }

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
        string dir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        Directory.CreateDirectory(dir);

        string manifestPath = Path.Combine(dir, "package.json");
        bool createdManifest = false;
        if (!File.Exists(manifestPath))
        {
            // 形态对齐上游 initProfile：private 工作区清单 + 有序 bundles 列表。
            // 手拼 JSON（字段全为自有常量，无转义面；缩进排版须与上游模板逐字对齐，紧凑源生成给不了）
            string bundles = string.Join(", ", InitialBundles.Select(b => "\"" + b + "\""));
            string json =
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

        string patchPath = Path.Combine(dir, "cordis.patch.yml");
        if (!File.Exists(patchPath))
        {
            File.WriteAllText(patchPath, PatchTemplate);
        }

        string workspacePath = Path.Combine(dir, "pnpm-workspace.yaml");
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
        string dir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        string manifestPath = Path.Combine(dir, "package.json");
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
        foreach ((string? name, JsonNode? value) in deps)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out string? spec) && IsDeadLocalPath(spec, dir))
            {
                removable.Add((name, spec));
            }
        }

        if (removable.Count == 0)
        {
            return 0;
        }

        foreach ((string? name, string _) in removable)
        {
            deps.Remove(name);
        }

        // bundles 若存在但非数组（损坏态）：整体放弃移除，避免写回「dependencies 已删、bundles 残留」的
        // 半 reconcile 状态——半写会把不可解析引用留在 bundles，反而违背 reconcile 的初衷。
        JsonNode? profileNode = root["dsh"]?["profile"];
        if (profileNode is JsonObject profile && profile["bundles"] is not null && profile["bundles"] is not JsonArray)
        {
            log($"[host] 桌面 profile reconcile 放弃（bundles 结构损坏，非数组）：{manifestPath}");
            return 0;
        }

        if (root["dsh"]?["profile"]?["bundles"] is JsonArray bundles)
        {
            foreach ((string? name, string _) in removable)
            {
                var matches = bundles.Where(b =>
                    b is JsonValue v && v.TryGetValue<string>(out string? s) && s == name).ToList();
                foreach (JsonNode? m in matches)
                {
                    bundles.Remove(m);
                }
            }
        }

        MarketInstallHelper.WriteProfilePkg(manifestPath, root);
        foreach ((string? name, string? spec) in removable)
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

        string full = Path.IsPathRooted(target)
            ? target
            : Path.Combine(profileDir, target);
        return !File.Exists(full) && !Directory.Exists(full);
    }
}
