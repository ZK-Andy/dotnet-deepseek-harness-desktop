using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>随包插件后台安装的纯逻辑（可单测）：检测、workspace 修正、spec 解析、bundles 补写。</summary>
/// <remarks>随包插件当前仅 <c>dsh-desktop-companion</c>（桌面伴生，安装器资源供给、无 registry 回退）；
/// dshmarket 改由首启引导经 registry 安装（见 RuntimeBootstrap，online-first 批次三）。</remarks>
public static class MarketInstallHelper
{
    /// <summary>dshmarket 的 registry 直装 spec（@latest：市场内核跟随上游 dist-tag，无钉版）。
    /// 显式 @latest 对既存本地 seed 同样强制改写为 registry 形态（pnpm 对裸名 add 幂等、spec 永不翻转，
    /// ADR bundled-plugin-registry-normalization 实机实证）。</summary>
    public const string MarketSpec = "dshmarket@latest";

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

    /// <summary>解析桌面伴生插件的安装 <c>spec</c>：安装器资源 tgz（&gt;1K 防假包）；
    /// 无 registry 分发面，运行时目录种子随 online-first 退役（ADR online-first-unbundled-runtime 批次三）。</summary>
    /// <param name="installerPluginsDir">安装器自带的 resources/plugins（打包形态唯一供给源）。</param>
    /// <returns>可安装的 spec；<see langword="null"/> 表示无任何来源（如开发用 PATH dsh），调用方跳过。</returns>
    public static string? ResolveCompanionSpec(string? installerPluginsDir)
    {
        if (!string.IsNullOrWhiteSpace(installerPluginsDir))
        {
            var packaged = Path.Combine(installerPluginsDir, "dsh-desktop-companion.tgz");
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
        var dir = Path.GetDirectoryName(path) ?? ".";
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
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

    /// <summary>
    /// 首启引导经 registry 安装市场（ADR online-first-unbundled-runtime 批次三）：在 spawn dsh 前
    /// 经 <see cref="MarketSpec"/> 安装 dshmarket 到桌面 profile（新装 + 存量 seed 自愈归化）。
    /// 先放行 profile workspace 的 allowBuilds（dshmarket 依赖树含原生构建，pnpm 11 默认拒绝），
    /// 再 <c>dsh plugin add dshmarket@latest</c>（minimumReleaseAge 政策拒绝时放宽重试一次），
    /// 最后补写 bundles 兜底。best-effort：失败只留日志不抛（市场缺失不阻塞首启）。
    /// <paramref name="runPluginAdd"/> 是注入的子进程执行器（生产用真实 spawn，测试用 fake 断言参数/环境）。
    /// </summary>
    /// <param name="nodeExe">运行时的 node 可执行。</param>
    /// <param name="dshEntry">运行时 dsh bin.js 入口。</param>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <param name="log">诊断日志出口。</param>
    /// <param name="runPluginAdd">执行一次 <c>dsh plugin add</c> 的注入委托（接收已配好 env 的 ProcessStartInfo）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task EnsureMarketFromRegistryAsync(
        string nodeExe,
        string dshEntry,
        string dshHome,
        Action<string> log,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<(int Exit, string Out, string Err)>> runPluginAdd,
        CancellationToken ct)
    {
        // dshmarket 依赖树含原生构建；pnpm 11 默认拒绝，须先放行 allowBuilds（与随包安装同款自愈）
        var workspacePath = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName, "pnpm-workspace.yaml");
        EnsureWorkspaceAllowBuilds(workspacePath);

        System.Diagnostics.ProcessStartInfo BuildPsi(bool relaxPolicy)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = nodeExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(dshEntry);
            psi.ArgumentList.Add("plugin");
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add(HarnessRuntimeHost.DesktopProfileName);
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add(MarketSpec);
            psi.Environment["DSH_HOME"] = dshHome;
            if (relaxPolicy)
            {
                psi.Environment["pnpm_config_minimum_release_age"] = "0";
            }

            HarnessRuntimeHost.ApplyPnpmWriteDirs(psi, dshHome);
            HarnessRuntimeHost.UseUtf8TextStreams(psi);
            return psi;
        }

        async Task<(int Exit, string Out, string Err)> RunAsync(bool relaxPolicy)
            => await runPluginAdd(BuildPsi(relaxPolicy), ct).ConfigureAwait(false);

        log($"[host] 引导：registry 安装市场（{MarketSpec}）");
        var (exitCode, outText, errText) = await RunAsync(relaxPolicy: false).ConfigureAwait(false);
        log($"[host] dsh plugin add exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
        if (exitCode != 0 && (outText + errText).Contains("MINIMUM_RELEASE_AGE", StringComparison.OrdinalIgnoreCase))
        {
            log("[host] lockfile 被 minimumReleaseAge 政策拒绝；放宽该政策重试一次（仅本次安装生效）");
            (exitCode, outText, errText) = await RunAsync(relaxPolicy: true).ConfigureAwait(false);
            log($"[host] dsh plugin add(重试) exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
        }

        if (exitCode != 0)
        {
            log($"[host] 市场安装失败 exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}（市场缺失不阻塞首启，可稍后经设置/手动安装）");
            return;
        }

        var profilePkg = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName, "package.json");
        if (await EnsureBundlesContainsAsync(profilePkg, "dshmarket").ConfigureAwait(false))
        {
            log("[host] 已补写 bundles dshmarket");
        }
    }

    /// <summary>
    /// 在 spawn dsh 前安装待装随包插件（batch-1 对齐参照：所有插件内核前就位，绝不「安装后重启」）。
    /// 当前唯一随包插件 = companion（file: 安装器资源 spec）；dshmarket 已由
    /// <see cref="EnsureMarketFromRegistryAsync"/> 在引导内安装。best-effort：失败只留日志不抛
    /// （缺 companion 不阻塞 dsh 起动，下次启动自愈）。与 <see cref="EnsureMarketFromRegistryAsync"/>
    /// 同为注入 <paramref name="runPluginAdd"/> 的测试友好形态——生产用真实 spawn，测试用 fake 断言参数/环境。
    /// </summary>
    /// <param name="nodeExe">运行时的 node 可执行。</param>
    /// <param name="dshEntry">运行时 dsh bin.js 入口。</param>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <param name="installerPluginsDir">安装器自带插件资源目录（resources/plugins）；开发/引导形态可 null。</param>
    /// <param name="log">诊断日志出口。</param>
    /// <param name="runPluginAdd">执行一次 <c>dsh plugin add</c> 的注入委托。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task EnsureBundledPluginsBeforeSpawnAsync(
        string nodeExe,
        string dshEntry,
        string dshHome,
        string? installerPluginsDir,
        Action<string> log,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<(int Exit, string Out, string Err)>> runPluginAdd,
        CancellationToken ct)
    {
        var profileDir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        var profilePkg = Path.Combine(profileDir, "package.json");
        var pending = BundledPluginCatalog.AssemblePending(
            BundledPluginCatalog.All, installerPluginsDir, profilePkg, profileDir, log);
        if (pending.Count == 0)
        {
            log("[host] 随包插件无需安装（companion 已就位），跳过");
            return;
        }

        await CleanupBogusAppDependencyAsync(profilePkg).ConfigureAwait(false);
        EnsureWorkspaceAllowBuilds(Path.Combine(profileDir, "pnpm-workspace.yaml"));

        foreach (var (pkg, spec) in pending)
        {
            System.Diagnostics.ProcessStartInfo BuildPsi(bool relaxPolicy)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = nodeExe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(dshEntry);
                psi.ArgumentList.Add("plugin");
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add(HarnessRuntimeHost.DesktopProfileName);
                psi.ArgumentList.Add("add");
                psi.ArgumentList.Add(spec);
                psi.Environment["DSH_HOME"] = dshHome;
                if (relaxPolicy)
                {
                    psi.Environment["pnpm_config_minimum_release_age"] = "0";
                }

                HarnessRuntimeHost.ApplyPnpmWriteDirs(psi, dshHome);
                HarnessRuntimeHost.UseUtf8TextStreams(psi);
                return psi;
            }

            async Task<(int Exit, string Out, string Err)> RunAsync(bool relaxPolicy)
                => await runPluginAdd(BuildPsi(relaxPolicy), ct).ConfigureAwait(false);

            log($"[host] 随包插件安装（{pkg}）spec={spec}");
            var (exitCode, outText, errText) = await RunAsync(relaxPolicy: false).ConfigureAwait(false);
            log($"[host] dsh plugin add exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
            if (exitCode != 0 && (outText + errText).Contains("MINIMUM_RELEASE_AGE", StringComparison.OrdinalIgnoreCase))
            {
                log("[host] lockfile 被 minimumReleaseAge 政策拒绝；放宽该政策重试一次（仅本次安装生效）");
                (exitCode, outText, errText) = await RunAsync(relaxPolicy: true).ConfigureAwait(false);
                log($"[host] dsh plugin add(重试) exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
            }

            if (exitCode != 0)
            {
                log($"[host] 随包插件安装失败（{pkg}）exit={exitCode}（常见：ERR_PNPM_IGNORED_BUILDS——workspace 已自动修复，下次启动自愈；详情见 stderr）");
                continue;
            }

            if (await EnsureBundlesContainsAsync(profilePkg, pkg).ConfigureAwait(false))
            {
                log($"[host] 已补写 bundles {pkg}");
            }
        }

        // 桌面核心不变量：reconcile 无论怎么重整 bundles，web-app 层绝不能丢（丢了下次启动就没有 Web UI）
        foreach (var builtin in DesktopProfileBootstrap.InitialBundles)
        {
            if (await EnsureBundlesContainsAsync(profilePkg, builtin).ConfigureAwait(false))
            {
                log($"[host] 已补回桌面必需 bundle {builtin}");
            }
        }
    }
}
