using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// <see cref="RuntimeBootstrap"/> 的引擎辅助面（partial，ADR 尺寸健康闸）：子进程捕获、node 探测、
/// 步骤超时、失败态、node/npm-cli 定位、锁重试。引导状态机（RunAsync/DownloadNodeAsync）与
/// 纯函数/文件系统原子面分别在 <c>RuntimeBootstrap.cs</c>/<c>RuntimeBootstrap.Pure.cs</c>。
/// </summary>
public static partial class RuntimeBootstrap
{
    private static async Task<(int Exit, string Stdout, string Stderr)> RunCaptureAsync(
        Action<string> log, string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = StripExtendedPrefix(exe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // GUI 子系统壳 spawn tar/node/npm 不能闪控制台窗（Windows）
            CreateNoWindow = true,
        };
        HarnessRuntimeHost.UseUtf8TextStreams(psi);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // npm 专属：显式压低 V8 堆上限。dsh 依赖树（454 包）解析峰值 ~1.7-3GB，默认上限
        // （≈物理内存一半）在弱内存机器会 abort（exit 134，沙箱 8G 实证）；3072 强制积极 GC
        // 且留足解析空间。仅对 npm-cli 调用注入，不污染 node 运行 dsh 的堆行为
        if (args.Any(a => a.EndsWith("npm-cli.js", StringComparison.Ordinal)))
        {
            psi.Environment["NODE_OPTIONS"] = "--max-old-space-size=3072";
        }

        log?.Invoke($"[bootstrap] run: {psi.FileName} {string.Join(' ', args)}");
        using Process p = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程 {exe}");
        try
        {
            // 双流并发读：顺序先读 stdout 时 stderr 塞满 pipe buffer（~64KB）会互等死锁
            // （npm 崩溃 full report 可超限）。进程退出后两流必然收尾，收尾读取不再带 ct
            Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return (p.ExitCode, stdout, stderr);
        }
        catch (Exception)
        {
            // 取消/异常路径必须整树击杀：ReadToEndAsync 的 OCE 会跳过 WaitForExitAsync（其内部
            // 才注册 kill-on-cancel），using dispose 只关句柄不杀进程——npm 会带着 runtimeDir
            // 的写权成孤儿，下次引导与新进程竞写 node_modules
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已自行退出：无需击杀
            }

            throw;
        }
    }

    private static readonly Regex s_nodeVersionToken = new(
        @"^v(\d+)\.\d+\.\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static async Task<(string? NodePath, string? Version)> ProbeLocalNodeAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            (int exit, string? stdout, string _) = await RunCaptureAsync(log, "node", ["--version"], ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return (null, null);
            }

            Match m = s_nodeVersionToken.Match(stdout.Trim());
            return m.Success ? ("node", m.Groups[1].Value) : (null, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke($"[bootstrap] PATH node 探测失败（视为不存在）：{ex.Message}");
            return (null, null);
        }
    }

    /// <summary>步骤级超时包装：应用退出（appCt）取消仍以 OCE 上抛；仅步超时转异常走失败重试页。</summary>
    private static Task WithStepTimeout(int minutes, CancellationToken appCt, Func<CancellationToken, Task> action) =>
        WithStepTimeout<object?>(minutes, appCt, async token =>
        {
            await action(token).ConfigureAwait(false);
            return null;
        });

    private static async Task<T> WithStepTimeout<T>(int minutes, CancellationToken appCt, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        cts.CancelAfter(TimeSpan.FromMinutes(minutes));
        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!appCt.IsCancellationRequested)
        {
            throw new InvalidOperationException($"步骤超时（>{minutes} 分钟），网络停滞或资源受限，可重试");
        }
    }

    private static BootstrapOutcome Fail(BootstrapStep step, string error)
    {
        // 失败进度由调用方在重试循环推送（此处返回即可，避免双通道）
        return new BootstrapOutcome(false, step, $"[{step}] {error}", null);
    }

    private static string? TryFindNode(string runtimeDir)
    {
        string node = Path.Combine(runtimeDir, "node");
        if (File.Exists(node))
        {
            return node;
        }

        string nodeExe = Path.Combine(runtimeDir, "node.exe");
        return File.Exists(nodeExe) ? nodeExe : null;
    }

    private static string? LocateNpmCliBesideLocalNode(string nodePath)
    {
        // node 在 PATH 上时布局不定（发行包/包管理器/symlink）；只探测两种主流布局，
        // 找不到 npm-cli 一律视为不可复用——绝不猜测执行
        string? dir = Path.GetDirectoryName(Path.GetFullPath(nodePath));
        if (dir is null)
        {
            return null;
        }

        string[] candidates = new[]
        {
            // 发行包布局：node 同级的 lib/node_modules（unix）或同级 node_modules（win）
            Path.Combine(dir, NpmCliRelativePath()),
            Path.Combine(dir, "..", NpmCliRelativePath()),
            Path.Combine(dir, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>引导第①相：Node 就位（优先已下载/本机复用，否则下载钉版）；返回 (nodePath, npmCli)。</summary>
    private static async Task<(string? NodePath, string? NpmCli)> EnsureNodePhaseAsync(
        RuntimeBootstrapOptions options, string runtimeDir, Action<BootstrapProgress> report,
        RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        // ① Node 就位：优先已下载（断点重试语义），再本机复用，最后下载钉版
        report(new BootstrapProgress(BootstrapStep.EnsureNode, "检测本机 Node"));
        string? nodePath = TryFindNode(runtimeDir);
        string? npmCli = null;
        if (nodePath is not null)
        {
            report(new BootstrapProgress(BootstrapStep.EnsureNode, "复用已下载的 Node"));
            npmCli = Path.Combine(runtimeDir, NpmCliRelativePath());
        }
        else
        {
            (string? localNode, string? localMajor) = await hooks.ProbeLocalNodeAsync(ct).ConfigureAwait(false);
            bool reuse = localNode is not null &&
                        int.TryParse(localMajor, out int major) &&
                        major >= options.MinimumLocalNodeMajor;
            if (reuse)
            {
                report(new BootstrapProgress(BootstrapStep.EnsureNode, $"复用本机 Node v{localMajor}+"));
                // 本机 node 的 npm-cli 紧邻其安装布局；找不到即视为不可复用（fail loud 落到下载）
                npmCli = LocateNpmCliBesideLocalNode(localNode!);
                if (npmCli is null)
                {
                    report(new BootstrapProgress(BootstrapStep.EnsureNode, "本机 Node 缺 npm，改用下载钉版"));
                    reuse = false;
                }
                else
                {
                    nodePath = localNode;
                }
            }

            if (!reuse)
            {
                (nodePath, npmCli) = await DownloadNodeAsync(options, runtimeDir, report, hooks, ct).ConfigureAwait(false);
            }
        }

        return (nodePath, npmCli);
    }

    /// <summary>下载相：从官方 SHASUMS256.txt 取可信摘要（不可达即中止，不用镜像自证）。</summary>
    private static async Task<string> FetchSha256ExpectedAsync(
        RuntimeBootstrapOptions options, string baseUrl, string versionDir, string fileName,
        Action<BootstrapProgress> report, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        // 摘要优先取自官方（信任根）：先拉 SHASUMS256.txt 得可信摘要，随后归档（官方→镜像）逐一用它校验；
        // 官方摘要不可达即中止（无可信摘要 → 不用镜像，防投毒，ADR 批次三）
        report(new BootstrapProgress(BootstrapStep.EnsureNode, "获取 Node 发行包 SHA256 摘要"));
        string shasums;
        try
        {
            shasums = await WithStepTimeout(options.StepTimeoutMinutes, ct,
                token => hooks.FetchTextAsync($"{baseUrl}/{versionDir}/SHASUMS256.txt", token)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 官方摘要不可达 = 无可信信任根，绝不回落镜像摘要（自证自证）——fail loud，带原始网络上下文
            throw new InvalidOperationException($"获取 Node 发行包官方 SHA256 摘要失败：{ex.Message}", ex);
        }

        return SelectSha256(shasums, fileName)
            ?? throw new InvalidOperationException($"SHASUMS256.txt 缺少 {fileName} 的摘要，安全中止");
    }

    /// <summary>解压 → 归一为扁平布局 → 原子 swap 进 runtimeDir（含清理 extract 残余；fail loud）。</summary>
    private static async Task ExtractNormalizeAndSwapAsync(
        RuntimeBootstrapOptions options, string stagingDir, string runtimeDir, string archivePath,
        Action<BootstrapProgress> report, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        report(new BootstrapProgress(BootstrapStep.ExtractNode, "解压 Node"));
        string extractDir = Path.Combine(stagingDir, "extract");
        Directory.CreateDirectory(extractDir);
        await WithStepTimeout(options.StepTimeoutMinutes, ct,
            token => hooks.ExtractArchiveAsync(archivePath, extractDir, token)).ConfigureAwait(false);

        // 归一为捆绑闭包同款扁平布局（RuntimeLocator.TryLocateBundled 的探测形态）于 staging 根：
        // node(.exe) 在根目录、npm 模块树按平台相对路径可达；发行包其余内容（include/share/
        // CHANGELOG 等）随 staging 清理，保持 runtimeDir 与闭包同构
        string inner = Directory.EnumerateDirectories(extractDir).FirstOrDefault()
            ?? throw new InvalidOperationException("Node 发行包解压结果无内容目录");
        string nodeRel = OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node");
        string npmTreeRel = OperatingSystem.IsWindows() ? "node_modules" : "lib";
        MoveInto(stagingDir, Path.Combine(inner, nodeRel), Path.GetFileName(nodeRel));
        MoveInto(stagingDir, Path.Combine(inner, npmTreeRel), npmTreeRel);
        // 发行包其余内容（include/share/CHANGELOG/LICENSE 等）仍留在 extract/<inner>；swap 会把整个
        // stagingDir 原子搬进 runtimeDir，必须先清 extract 残余，保证 runtimeDir 与闭包同构（R2 B1）。
        // 用 DeleteWithLockRetry（fail loud）而非最佳努力——清不动就不该把残余搬进正式目录
        DeleteWithLockRetry(extractDir);

        report(new BootstrapProgress(BootstrapStep.ExtractNode, "原子就位运行时"));
        SwapStagingIntoPlace(stagingDir, runtimeDir);
    }
}
