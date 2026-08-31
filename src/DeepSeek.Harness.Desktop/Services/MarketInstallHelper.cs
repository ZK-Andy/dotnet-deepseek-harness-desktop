namespace DeepSeek.Harness.Desktop.Services;

/// <summary>随包插件后台安装的纯逻辑（可单测）：安装驱动（registry 市场 + 随包插件 spawn 前安装）。
/// JSON/profile 文件维护辅助见 <c>MarketInstallHelper.Json.cs</c>（partial）。</summary>
/// <remarks>随包插件当前仅 <c>dsh-desktop-companion</c>（桌面伴生，安装器资源供给、无 registry 回退）；
/// dshmarket 改由首启引导经 registry 安装（见 RuntimeBootstrap，online-first 批次三）。</remarks>
public static partial class MarketInstallHelper
{
    /// <summary>dshmarket 的 registry 直装 spec（@latest：市场内核跟随上游 dist-tag，无钉版）。
    /// 显式 @latest 对既存本地 seed 同样强制改写为 registry 形态（pnpm 对裸名 add 幂等、spec 永不翻转，
    /// ADR bundled-plugin-registry-normalization 实机实证）。</summary>
    public const string MarketSpec = "dshmarket@latest";

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
        string workspacePath = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName, "pnpm-workspace.yaml");
        EnsureWorkspaceAllowBuilds(workspacePath);

        log($"[host] 引导：registry 安装市场（{MarketSpec}）");
        (int exitCode, string? outText, string? errText) =
            await RunPluginAddAsync(nodeExe, dshEntry, dshHome, MarketSpec, log, runPluginAdd, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            log($"[host] 市场安装失败 exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}（市场缺失不阻塞首启，可稍后经设置/手动安装）");
            return;
        }

        string profilePkg = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName, "package.json");
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
    /// <param name="nodeExe">运行时的 node 可执行；<see langword="null"/> 时用 PATH 上的 <c>dsh</c> 命令
    /// （PATH-dsh 运行时形态，等价宿主 <c>HarnessRuntimeHost</c> spawn 时的 <c>dsh</c> 命令解析）。</param>
    /// <param name="dshEntry">运行时 dsh bin.js 入口；<paramref name="nodeExe"/> 为 null（PATH dsh）时无入口参数。</param>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <param name="installerPluginsDir">安装器自带插件资源目录（resources/plugins）；开发/引导形态可 null。</param>
    /// <param name="log">诊断日志出口。</param>
    /// <param name="runPluginAdd">执行一次 <c>dsh plugin add</c> 的注入委托。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task EnsureBundledPluginsBeforeSpawnAsync(
        string? nodeExe,
        string? dshEntry,
        string dshHome,
        string? installerPluginsDir,
        Action<string> log,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<(int Exit, string Out, string Err)>> runPluginAdd,
        CancellationToken ct)
    {
        string profileDir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        string profilePkg = Path.Combine(profileDir, "package.json");
        List<(string Package, string Spec)> pending = BundledPluginCatalog.AssemblePending(
            BundledPluginCatalog.All, installerPluginsDir, profilePkg, profileDir, log);
        if (pending.Count == 0)
        {
            log("[host] 随包插件无需安装（companion 已就位），跳过");
            return;
        }

        await CleanupBogusAppDependencyAsync(profilePkg).ConfigureAwait(false);
        EnsureWorkspaceAllowBuilds(Path.Combine(profileDir, "pnpm-workspace.yaml"));

        foreach ((string? pkg, string? spec) in pending)
        {
            log($"[host] 随包插件安装（{pkg}）spec={spec}");
            (int exitCode, string? outText, string? errText) =
                await RunPluginAddAsync(nodeExe, dshEntry, dshHome, spec, log, runPluginAdd, ct).ConfigureAwait(false);
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
        foreach (string builtin in DesktopProfileBootstrap.InitialBundles)
        {
            if (await EnsureBundlesContainsAsync(profilePkg, builtin).ConfigureAwait(false))
            {
                log($"[host] 已补回桌面必需 bundle {builtin}");
            }
        }
    }

    /// <summary>
    /// 构建并运行一次 <c>dsh plugin add</c>（写入桌面 profile）：minReleaseAge 政策拒绝时放宽重试一次。
    /// BuildPsi 与 RunOnce 在此集中（两个安装驱动共用，消除重复）；<paramref name="nodeExe"/> 为 null =
    /// PATH-dsh 运行时（用 PATH 上 dsh 命令，无独立入口参数）。
    /// </summary>
    private static async Task<(int Exit, string Out, string Err)> RunPluginAddAsync(
        string? nodeExe,
        string? dshEntry,
        string dshHome,
        string spec,
        Action<string> log,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<(int Exit, string Out, string Err)>> runPluginAdd,
        CancellationToken ct)
    {
        System.Diagnostics.ProcessStartInfo BuildPsi(bool relaxPolicy)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = nodeExe is null ? "dsh" : nodeExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (dshEntry is not null)
            {
                psi.ArgumentList.Add(dshEntry);
            }
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

            HarnessRuntimeHost.UseUtf8TextStreams(psi);
            return psi;
        }

        async Task<(int Exit, string Out, string Err)> RunOnce(bool relaxPolicy)
            => await runPluginAdd(BuildPsi(relaxPolicy), ct).ConfigureAwait(false);

        (int exitCode, string? outText, string? errText) = await RunOnce(relaxPolicy: false).ConfigureAwait(false);
        log($"[host] dsh plugin add exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
        if (exitCode != 0 && (outText + errText).Contains("MINIMUM_RELEASE_AGE", StringComparison.OrdinalIgnoreCase))
        {
            log("[host] lockfile 被 minimumReleaseAge 政策拒绝；放宽该政策重试一次（仅本次安装生效）");
            (exitCode, outText, errText) = await RunOnce(relaxPolicy: true).ConfigureAwait(false);
            log($"[host] dsh plugin add(重试) exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
        }

        return (exitCode, outText, errText);
    }
}
