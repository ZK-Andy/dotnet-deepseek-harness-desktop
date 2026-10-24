namespace DeepSeek.Harness.Desktop.Infrastructure.Plugins;

/// <summary>随包插件后台安装的纯逻辑（可单测）：随包插件 spawn 前安装驱动。
/// JSON/profile 文件维护辅助见 <c>MarketInstallHelper.Json.cs</c>（partial）。</summary>
/// <remarks>随包插件当前仅 <c>dsh-desktop-companion</c>（桌面伴生，安装器资源供给、无 registry 回退）。</remarks>
public static partial class MarketInstallHelper
{
    /// <summary>
    /// 在 spawn dsh 前安装待装随包插件（batch-1 对齐参照：所有插件内核前就位，绝不「安装后重启」）。
    /// 当前唯一随包插件 = companion（file: 安装器资源 spec）。事务化（ADR transactional-plugin-pipeline）：
    /// 变更加在 <see cref="PluginProfileTransaction"/> 的 staging 副本上，任一插件装成功即 staged 探针
    /// 验证 <c>dsh web:</c> 可出后 journal 换入——探针不过 = staging 作废、active 保持旧完整态。
    /// best-effort：失败只留日志不抛（缺 companion 不阻塞 dsh 起动，下次启动重试整装）。
    /// </summary>
    /// <param name="nodeExe">运行时的 node 可执行；<see langword="null"/> 时用 PATH 上的 <c>dsh</c> 命令
    /// （PATH-dsh 运行时形态，等价宿主 <c>HarnessRuntimeHost</c> spawn 时的 <c>dsh</c> 命令解析）。</param>
    /// <param name="dshEntry">运行时 dsh bin.js 入口；<paramref name="nodeExe"/> 为 null（PATH dsh）时无入口参数。</param>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <param name="installerPluginsDir">安装器自带插件资源目录（resources/plugins）；开发/引导形态可 null。</param>
    /// <param name="log">诊断日志出口。</param>
    /// <param name="runPluginAdd">进程执行注入委托（执行一次子命令并回传退出码与 stdout/stderr；
    /// 除 dsh plugin add 外也承载 pnpm 前置的 <c>pnpm --version</c> / <c>npm install -g pnpm</c>）。</param>
    /// <param name="runProbe">staged 体检探针委托（生产用 <see cref="PluginProcessRunner.RunProbeAsync"/>；
    /// 返回 null = 探针未出 URL → 放弃激活）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次是否确有插件装成功并换入 active（无待装/全失败/探针不过返回 false）。</returns>
    public static async Task<bool> EnsureBundledPluginsBeforeSpawnAsync(
        string? nodeExe,
        string? dshEntry,
        string dshHome,
        string? installerPluginsDir,
        Action<string> log,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<(int Exit, string Out, string Err)>> runPluginAdd,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<Uri?>> runProbe,
        CancellationToken ct)
    {
        string profileDir = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        string profilePkg = Path.Combine(profileDir, "package.json");
        List<(string Package, string Spec)> pending = BundledPluginCatalog.AssemblePending(
            BundledPluginSupply.All, installerPluginsDir, profilePkg, profileDir, new PluginPackageReader(), log);
        if (pending.Count == 0)
        {
            log("[host] 随包插件无需安装（companion 已就位），跳过");
            return false;
        }

        // pnpm 前置（ADR companion-pnpm-prerequisite）：失败即装配放弃（best-effort 不变）。
        if (!await EnsurePnpmAsync(runPluginAdd, log, ct).ConfigureAwait(false))
        {
            log("[host] 随包插件装配前置失败（pnpm 不可用且自动安装未成）——本次放弃，下次启动重试");
            return false;
        }

        var tx = PluginProfileTransaction.Begin(dshHome, log);
        try
        {
            string stagingPkg = Path.Combine(tx.StagingProfileDir, "package.json");
            await CleanupBogusAppDependencyAsync(stagingPkg).ConfigureAwait(false);
            EnsureWorkspaceAllowBuilds(Path.Combine(tx.StagingProfileDir, "pnpm-workspace.yaml"));

            bool anyInstalled = false;
            foreach ((string? pkg, string? spec) in pending)
            {
                log($"[host] 随包插件安装（{pkg}）spec={spec}");
                (int exitCode, string? outText, string? errText) =
                    await RunPluginAddAsync(nodeExe, dshEntry, tx.StagingHome, spec, log, runPluginAdd, ct).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    log($"[host] 随包插件安装失败（{pkg}）exit={exitCode}（常见：ERR_PNPM_IGNORED_BUILDS——workspace 已在 staging 修复，下次启动重试整装；详情见 stderr）");
                    continue;
                }

                anyInstalled = true;
                if (await EnsureBundlesContainsAsync(stagingPkg, pkg).ConfigureAwait(false))
                {
                    log($"[host] 已补写 bundles {pkg}（staging）");
                }
            }

            if (!anyInstalled)
            {
                // 全部失败：staging 作废、active 保持旧完整态（下次启动重试整装）
                tx.Discard();
                return false;
            }

            await BackfillDesktopBundlesAsync(stagingPkg, log, ct).ConfigureAwait(false);

            // staged 体检：换入前先证明 staging 能活；不过 = 放弃激活
            if (await runProbe(PluginInstallProbe.BuildProbePsi(tx.StagingHome), ct).ConfigureAwait(false) is null)
            {
                log("[host] staged 体检探针未出 URL：放弃激活，active 保持旧完整态");
                tx.Discard();
                return false;
            }

            tx.Activate();
            return true;
        }
        catch
        {
            // 异常路径（含取消）：staging 收口，active 全程未被触碰；异常本身上抛由调用方收口
            tx.Discard();
            throw;
        }
    }

    /// <summary>staging 的桌面核心 bundles 补回（换入前不变量：无论怎么补写，web-app 层绝不能丢——
    /// 丢了换入后就没有 Web UI）。</summary>
    private static async Task BackfillDesktopBundlesAsync(string stagingPkg, Action<string> log, CancellationToken ct)
    {
        foreach (string builtin in DesktopProfileBootstrap.InitialBundles)
        {
            if (await EnsureBundlesContainsAsync(stagingPkg, builtin).ConfigureAwait(false))
            {
                log($"[host] 已补回桌面必需 bundle {builtin}（staging）");
            }
        }
    }

    /// <summary>
    /// 装配前置 ensure-pnpm（ADR companion-pnpm-prerequisite）：PATH 有真 pnpm 即用（记版本）；
    /// 未检出则 <c>npm install -g pnpm</c> 装到与 dsh 同一全局位后复验。探测/安装经注入的进程委托
    /// （与 plugin add 同一 runner）。失败返回 false——装配放弃由调用方收口（best-effort）。
    /// 可执行缺失时 .NET Process.Start 抛 Win32Exception 而非返回非零（run `40136296983` CI 实证：
    /// 无 pnpm 环境探测即抛）——按 127（command-not-found 惯用码）收口继续走安装，不上抛。
    /// </summary>
    private static async Task<bool> EnsurePnpmAsync(
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<(int Exit, string Out, string Err)>> run,
        Action<string> log,
        CancellationToken ct)
    {
        async Task<(int Exit, string Out, string Err)> TryRunAsync(System.Diagnostics.ProcessStartInfo psi)
        {
            try
            {
                return await run(psi, ct).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Win32Exception 也覆盖非缺失形态（ERROR_ACCESS_DENIED=5、坏映像=193）——保真 ex.Message
                return (127, string.Empty, $"start failed: {ex.Message}");
            }
        }

        (int exit, string outText, _) = await TryRunAsync(BuildToolPsi("pnpm", "--version")).ConfigureAwait(false);
        if (exit == 0)
        {
            log($"[host] pnpm 前置：PATH 已有（{outText.Trim()}）");
            return true;
        }

        log("[host] pnpm 前置：未检出，npm install -g pnpm（与 dsh 同装到全局）");
        (exit, _, string errText) = await TryRunAsync(BuildToolPsi("npm", "install", "-g", "pnpm")).ConfigureAwait(false);
        if (exit != 0)
        {
            log($"[host] pnpm 前置安装失败（npm install -g pnpm exit={exit}）：{errText.Trim()}");
            return false;
        }

        (exit, outText, _) = await TryRunAsync(BuildToolPsi("pnpm", "--version")).ConfigureAwait(false);
        if (exit != 0)
        {
            log($"[host] pnpm 前置复验失败（安装后 pnpm --version exit={exit}）");
            return false;
        }

        log($"[host] pnpm 前置：已装到全局（{outText.Trim()}）");
        return true;
    }

    /// <summary>裸名工具命令的跨平台 psi（经 <see cref="Runtime.RuntimeBootstrap.RelayFor"/> 唯一家
    /// 中转：Windows 上 CreateProcess 不走 PATHEXT，裸 <c>pnpm</c>/<c>npm</c>（.cmd）起不来，经
    /// <c>cmd.exe</c> 让 cmd 做 PATH/PATHEXT 解析；Unix 直跑裸名）。</summary>
    private static System.Diagnostics.ProcessStartInfo BuildToolPsi(string fileName, params string[] args)
    {
        Runtime.DshCommand cmd = Runtime.RuntimeBootstrap.RelayFor(fileName, args, OperatingSystem.IsWindows());
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = cmd.Exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        EnvironmentHygiene.StripInherited(psi);
        foreach (string a in cmd.Args)
        {
            psi.ArgumentList.Add(a);
        }

        HarnessRuntimeHost.UseUtf8TextStreams(psi);
        return psi;
    }

    /// <summary>
    /// 构建并运行一次 <c>dsh plugin add</c>（写入桌面 profile）：minReleaseAge 政策拒绝时放宽重试一次。
    /// BuildPsi 与 RunOnce 在此集中；<paramref name="nodeExe"/> 为 null = PATH-dsh 运行时
    /// （用 PATH 上 dsh 命令，无独立入口参数）。
    /// </summary>
    internal static async Task<(int Exit, string Out, string Err)> RunPluginAddAsync(
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
            // node 直跑位优先（node 直接跑 JS 入口，无 shell 问题）；否则经统一 dsh 启动命令
            // （Windows 走 cmd 中转，裸名直跑起不来）。
            List<string> inner = PluginArgs(dshEntry, spec);
            Runtime.DshCommand? cmd = nodeExe is null
                ? Runtime.RuntimeBootstrap.DshCommandFor(null, inner)
                : null;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cmd?.Exe ?? nodeExe!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            EnvironmentHygiene.StripInherited(psi);
            System.Collections.Generic.IReadOnlyList<string> outer = cmd?.Args ?? inner;
            foreach (string a in outer)
            {
                psi.ArgumentList.Add(a);
            }
            psi.Environment["DSH_HOME"] = dshHome;
            if (relaxPolicy)
            {
                psi.Environment["pnpm_config_minimum_release_age"] = "0";
            }

            HarnessRuntimeHost.UseUtf8TextStreams(psi);
            return psi;
        }

        async Task<(int Exit, string Out, string Err)> RunOnceAsync(bool relaxPolicy)
            => await runPluginAdd(BuildPsi(relaxPolicy), ct).ConfigureAwait(false);

        (int exitCode, string? outText, string? errText) = await RunOnceAsync(relaxPolicy: false).ConfigureAwait(false);
        log($"[host] dsh plugin add exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
        if (exitCode != 0 && (outText + errText).Contains("MINIMUM_RELEASE_AGE", StringComparison.OrdinalIgnoreCase))
        {
            log("[host] lockfile 被 minimumReleaseAge 政策拒绝；放宽该政策重试一次（仅本次安装生效）");
            (exitCode, outText, errText) = await RunOnceAsync(relaxPolicy: true).ConfigureAwait(false);
            log($"[host] dsh plugin add(重试) exit={exitCode} stdout={outText.Trim()} stderr={errText.Trim()}");
        }

        return (exitCode, outText, errText);
    }

    /// <summary>插件安装内层参数（dsh 入口 + plugin --profile … add spec），供直跑与统一命令两路复用。</summary>
    private static List<string> PluginArgs(string? dshEntry, string spec)
    {
        var args = new List<string>();
        if (dshEntry is not null)
        {
            args.Add(dshEntry);
        }

        args.Add("plugin");
        args.Add("--profile");
        args.Add(HarnessRuntimeHost.DesktopProfileName);
        args.Add("add");
        args.Add(spec);
        return args;
    }
}
