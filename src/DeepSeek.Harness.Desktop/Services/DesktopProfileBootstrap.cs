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
}
