namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 首启引导状态机（ADR simple-shell-single-global-dsh）：桌面是简单壳，依赖全机唯一的系统全局 node +
/// 全局 dsh（都在 PATH 上）。有系统 node → 用它执行 <c>npm install -g @deepseek-ai/dsh@alpha</c>；没系统
/// node → 下载最新官方 node 发行包（SHA256 校验 + 多源回落）并**装到系统全局前缀**（用户可写优先，避免需
/// sudo；要 sudo 则提示用户手动命令），再装全局 dsh。装好的 node 落到系统全局位，桌面+终端共用同一份。
/// <c>npm install -g</c> 因权限需 sudo 时给出可手动执行的命令（不静默失败）。
/// 单次尝试语义：失败返回 <see cref="BootstrapOutcome"/> 非成功，重试循环由调用方驱动
/// （重试信号来自引导页的 desktop.bootstrap.retry 命令）。
/// 引擎辅助（进程捕获/探测/超时）与纯函数/文件系统原子面分别在
/// <c>RuntimeBootstrap.Engine.cs</c>/<c>RuntimeBootstrap.Pure.cs</c>（partial）。
/// </summary>
public static partial class RuntimeBootstrap
{
    /// <summary>生产 hooks：HttpClient 下载（断点续传 Range）/取文本、tar 解压、子进程直跑、PATH node 探测。</summary>
    public static RuntimeBootstrapHooks CreateDefaultHooks(Action<string> log)
    {
        return new RuntimeBootstrapHooks(
            DownloadFileAsync: async (url, dest, ct) =>
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                await DownloadResumableAsync(http, url, dest, ct).ConfigureAwait(false);
            },
            FetchTextAsync: async (url, ct) =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                return await http.GetStringAsync(url, ct).ConfigureAwait(false);
            },
            ExtractArchiveAsync: async (archive, destDir, ct) =>
            {
                (int exit, string _, string? stderr) = await RunCaptureAsync(
                    log, "tar", ["-xf", StripExtendedPrefix(archive), "-C", StripExtendedPrefix(destDir)], ct).ConfigureAwait(false);
                if (exit != 0)
                {
                    throw new InvalidOperationException($"tar 解压失败 exit={exit}：{stderr.Trim()}");
                }
            },
            RunProcessAsync: (exe, args, ct) => RunCaptureAsync(log, exe, args, ct),
            ProbeLocalNodeAsync: ct => ProbeLocalNodeAsync(log, ct));
    }

    /// <summary>执行一次引导尝试（确保系统全局 node + 全局 dsh 就位到 alpha）。</summary>
    public static async Task<BootstrapOutcome> RunAsync(
        RuntimeBootstrapOptions options,
        Action<BootstrapProgress> report,
        RuntimeBootstrapHooks hooks,
        CancellationToken ct)
    {
        // 每步超时（StepTimeoutMinutes，R2 评审 B3：网络停滞时无限 spinner 无出路）。
        // 步超时转 InvalidOperationException 走失败重试页；应用退出（parent ct）仍是 OCE 上抛
        BootstrapStep step = BootstrapStep.EnsureNode;
        try
        {
            // ① 确保系统全局 node（复用 PATH / 复用已装全局 / 下载装到系统全局）
            NodeResult node = await EnsureGlobalNodeAsync(options, report, hooks, ct).ConfigureAwait(false);

            // ② 经该 node 的 npm 把 dsh 装到系统全局位（装 / 更新到 @alpha）
            step = BootstrapStep.InstallDsh;
            report(new BootstrapProgress(BootstrapStep.InstallDsh, $"安装 dsh（{options.DshSpec}）"));
            (int exit, string? stdout, string? stderr) = await RunNpmInstallGlobalAsync(options, node, hooks, ct).ConfigureAwait(false);
            if (exit != 0)
            {
                string errText = string.IsNullOrEmpty(stderr) ? stdout ?? string.Empty : stderr;
                // 权限不足（npm 全局位需 sudo）：不静默失败，提示用户手动执行一条安装命令。
                if (IsPermissionError(stdout, stderr))
                {
                    return Fail(BootstrapStep.InstallDsh, $"npm install 需要提升权限。请在终端手动执行：sudo npm install -g {options.DshSpec}（{errText.Trim()}）");
                }

                return Fail(BootstrapStep.InstallDsh, $"npm install 失败 exit={exit}：{errText.Trim()}");
            }

            // ③ 验证 PATH dsh --version 可解析（全局 dsh 落位）
            step = BootstrapStep.VerifyDsh;
            report(new BootstrapProgress(BootstrapStep.VerifyDsh, "验证 dsh 版本"));
            string? version = await VerifyDshAsync(options, hooks, ct).ConfigureAwait(false);
            if (version is null)
            {
                // 定位提示（R2#2 边界）：npm 全局前缀可能与 node bin 不一致（~/.npmrc 自定义 prefix /
                // apt root-owned node），dsh 不在 PATH → 指引用户核对 npm 全局 bin。
                return Fail(BootstrapStep.VerifyDsh,
                    $"安装后未能解析全局 dsh 版本（{options.DshSpec}）。请确认该 node 的 npm 全局 bin（`npm config get prefix` 下的 bin）已加入 PATH，或手动执行 `npm install -g {options.DshSpec}`。");
            }

            report(new BootstrapProgress(BootstrapStep.Ready, $"dsh {version} 就绪"));
            return new BootstrapOutcome(true, BootstrapStep.Ready, null, version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(step, ex.Message);
        }
    }

    /// <summary>确保系统全局 node：PATH 复用 → 已装系统全局复用 → 下载最新官方 node 装到系统全局前缀。</summary>
    private static async Task<NodeResult> EnsureGlobalNodeAsync(
        RuntimeBootstrapOptions options, Action<BootstrapProgress> report, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        report(new BootstrapProgress(BootstrapStep.EnsureNode, "检测系统全局 Node"));
        (string? nodePath, string? npmCli) = await hooks.ProbeLocalNodeAsync(ct).ConfigureAwait(false);
        if (nodePath is not null && npmCli is not null)
        {
            report(new BootstrapProgress(BootstrapStep.EnsureNode, "复用系统全局 Node"));
            return new NodeResult(nodePath, npmCli);
        }

        // PATH 无 node：查系统全局前缀是否已有装好的 node（此前安装/用户手动），有则复用并把其 bin 暴露到 PATH
        string prefix = ResolveNodeGlobalPrefix(options);
        if (TryLocateNodeAtPrefix(prefix) is (string installedNode, string installedNpmCli))
        {
            string nodeBinDir = NodeBinDir(prefix);
            PrependPathToProcessEnv(nodeBinDir);
            report(new BootstrapProgress(BootstrapStep.EnsureNode, "复用已装到系统全局的 Node"));
            return new NodeResult(installedNode, installedNpmCli);
        }

        // 都没有：下载最新官方 node 并装到系统全局前缀
        report(new BootstrapProgress(BootstrapStep.EnsureNode, "无系统 Node，下载装到系统全局"));
        return await InstallGlobalNodeAsync(options, prefix, report, hooks, ct).ConfigureAwait(false);
    }

    /// <summary>下载最新官方 node 发行包 → 解压 → 把 bin/lib 落进系统全局前缀；权限不足则提示用户手动命令。</summary>
    private static async Task<NodeResult> InstallGlobalNodeAsync(
        RuntimeBootstrapOptions options, string prefix, Action<BootstrapProgress> report, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        string nodeVersion = await ResolveLatestNodeVersionAsync(options, hooks, ct).ConfigureAwait(false);
        string fileName = NodeArchiveFileName(nodeVersion)
            ?? throw new InvalidOperationException("当前平台无对应的 Node 发行包坐标（fail loud）");
        string baseUrl = options.NodeDistBaseUrl.TrimEnd('/');
        string versionDir = $"v{nodeVersion}";
        string workRoot = Path.Combine(Path.GetTempPath(), "dsh-node-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            // 摘要优先取自官方（信任根）：官方摘要不可达即中止（无可信摘要 → 不用镜像，防投毒）
            string expected = await FetchSha256ExpectedAsync(options, baseUrl, versionDir, fileName, report, hooks, ct).ConfigureAwait(false);

            string archivePath = Path.Combine(workRoot, fileName);
            var candidates = new List<string> { $"{baseUrl}/{versionDir}/{fileName}" };
            if (!string.IsNullOrWhiteSpace(options.NodeMirrorBaseUrl))
            {
                candidates.Add($"{options.NodeMirrorBaseUrl.TrimEnd('/')}/{versionDir}/{fileName}");
            }

            report(new BootstrapProgress(BootstrapStep.EnsureNode, $"下载 Node {nodeVersion}"));
            await WithStepTimeout(options.StepTimeoutMinutes, ct,
                token => DownloadWithFallbackAsync(candidates, archivePath, hooks, token)).ConfigureAwait(false);

            report(new BootstrapProgress(BootstrapStep.EnsureNode, "校验 SHA256"));
            string actual = await Sha256FileAsync(archivePath, ct).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Node 发行包 SHA256 不匹配（期望 {ShortHash(expected)}，实际 {ShortHash(actual)}），安全中止");
            }

            report(new BootstrapProgress(BootstrapStep.EnsureNode, "解压 Node"));
            string extractDir = Path.Combine(workRoot, "extract");
            Directory.CreateDirectory(extractDir);
            await WithStepTimeout(options.StepTimeoutMinutes, ct,
                token => hooks.ExtractArchiveAsync(archivePath, extractDir, token)).ConfigureAwait(false);
            string inner = Directory.EnumerateDirectories(extractDir).FirstOrDefault()
                ?? throw new InvalidOperationException("Node 发行包解压结果无内容目录");

            // 落位到系统全局前缀：把发行包的 bin/lib 拷进 <prefix>/bin、<prefix>/lib
            InstallNodeDistIntoPrefix(inner, prefix);

            string nodePath = Path.Combine(prefix, OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node"));
            string npmCli = Path.Combine(prefix, NpmCliRelativePath());
            if (!File.Exists(nodePath) || !File.Exists(npmCli))
            {
                throw new InvalidOperationException($"node 装到系统全局 {prefix} 后入口缺失（布局异常）");
            }

            string nodeBinDir = NodeBinDir(prefix);
            PrependPathToProcessEnv(nodeBinDir);
            report(new BootstrapProgress(BootstrapStep.EnsureNode, $"系统全局 Node 已就位（{prefix}）"));
            return new NodeResult(nodePath, npmCli);
        }
        finally
        {
            CleanupDirectory(workRoot);
        }
    }

    /// <summary>把 Node 发行包根目录按平台布局拷进系统全局前缀：unix 拷 bin/lib，Windows 拷 node_modules/node.exe。
    /// 失败（权限/IO）翻译为"需管理员"提示。</summary>
    private static void InstallNodeDistIntoPrefix(string nodeDistRoot, string prefix)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                CopyDirectoryRecursive(Path.Combine(nodeDistRoot, "node_modules"), Path.Combine(prefix, "node_modules"));
                string srcNode = Path.Combine(nodeDistRoot, "node.exe");
                if (File.Exists(srcNode))
                {
                    File.Copy(srcNode, Path.Combine(prefix, "node.exe"), overwrite: true);
                }
            }
            else
            {
                CopyDirectoryRecursive(Path.Combine(nodeDistRoot, "bin"), Path.Combine(prefix, "bin"));
                CopyDirectoryRecursive(Path.Combine(nodeDistRoot, "lib"), Path.Combine(prefix, "lib"));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"无法写入系统全局 node 安装位 {prefix}（权限不足：{ex.Message}）。请以管理员权限安装 Node.js（https://nodejs.org 官方安装包/系统包管理器），或放开 {prefix} 写入权限后重试。");
        }
    }

    /// <summary>解析要下载/安装的 node 版本：从 <c>NodeDistBaseUrl/index.json</c> 解析最新（"取最新"）。</summary>
    private static async Task<string> ResolveLatestNodeVersionAsync(
        RuntimeBootstrapOptions options, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        string indexJson = await WithStepTimeoutAsync(options.StepTimeoutMinutes, ct,
            token => hooks.FetchTextAsync(LatestNodeIndexUrl(options.NodeDistBaseUrl), token)).ConfigureAwait(false);
        return ParseLatestNodeVersion(indexJson)
            ?? throw new InvalidOperationException("无法从 nodejs.org index.json 解析最新 Node 版本");
    }

    /// <summary>下载相：从官方 SHASUMS256.txt 取可信摘要（不可达即中止，不用镜像自证）。</summary>
    private static async Task<string> FetchSha256ExpectedAsync(
        RuntimeBootstrapOptions options, string baseUrl, string versionDir, string fileName,
        Action<BootstrapProgress> report, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        report(new BootstrapProgress(BootstrapStep.EnsureNode, "获取 Node 发行包 SHA256 摘要"));
        string shasums;
        try
        {
            shasums = await WithStepTimeoutAsync(options.StepTimeoutMinutes, ct,
                token => hooks.FetchTextAsync($"{baseUrl}/{versionDir}/SHASUMS256.txt", token)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"获取 Node 发行包官方 SHA256 摘要失败：{ex.Message}", ex);
        }

        return SelectSha256(shasums, fileName)
            ?? throw new InvalidOperationException($"SHASUMS256.txt 缺少 {fileName} 的摘要，安全中止");
    }

    /// <summary>解析系统全局 node 安装前缀：<c>DSH_DESKTOP_NODE_GLOBAL_PREFIX</c> &gt; <see cref="RuntimeBootstrapOptions.NodeGlobalPrefix"/> &gt; <see cref="DefaultGlobalNodePrefix"/>。</summary>
    internal static string ResolveNodeGlobalPrefix(RuntimeBootstrapOptions options)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("DSH_DESKTOP_NODE_GLOBAL_PREFIX");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return string.IsNullOrWhiteSpace(options.NodeGlobalPrefix)
            ? DefaultGlobalNodePrefix()
            : Path.GetFullPath(options.NodeGlobalPrefix);
    }

    /// <summary>解析"当前活跃"的系统全局 node bin 目录（node 已装在该全局前缀）。未命中返回 null。</summary>
    internal static string? TryResolveActiveNodeBinDir(RuntimeBootstrapOptions options)
    {
        string binDir = NodeBinDir(ResolveNodeGlobalPrefix(options));
        bool nodePresent = OperatingSystem.IsWindows()
            ? File.Exists(Path.Combine(binDir, "node.exe"))
            : File.Exists(Path.Combine(binDir, "node"));
        return nodePresent ? binDir : null;
    }

    /// <summary>判某系统全局前缀下 node 已就位，返回 (node 可执行, npm-cli)。</summary>
    private static (string NodePath, string NpmCli)? TryLocateNodeAtPrefix(string prefix)
    {
        string nodePath = Path.Combine(prefix, OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node"));
        string npmCli = Path.Combine(prefix, NpmCliRelativePath());
        return File.Exists(nodePath) && File.Exists(npmCli) ? (nodePath, npmCli) : null;
    }

    /// <summary>把 <paramref name="dir"/> 前置进当前进程 PATH（幂等：已含则不动），让宿主子进程与后续命令可解析它。</summary>
    internal static void PrependPathToProcessEnv(string dir)
    {
        string existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] parts = existing.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Environment.SetEnvironmentVariable("PATH",
            existing.Length == 0 ? dir : dir + Path.PathSeparator + existing);
    }

    /// <summary>执行 <c>npm install -g</c>（装 / 更新 dsh 到系统全局位；用该 node 的 npm-cli，默认前缀 = 该 node 的全局前缀）。</summary>
    private static async Task<(int Exit, string? Stdout, string? Stderr)> RunNpmInstallGlobalAsync(
        RuntimeBootstrapOptions options, NodeResult node, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        var args = new List<string>
        {
            node.NpmCli,
            "install",
            "-g",
            "--no-audit",
            "--no-fund",
            "--loglevel=error",
            options.DshSpec,
        };

        return await WithStepTimeoutAsync(options.StepTimeoutMinutes, ct, token => hooks.RunProcessAsync(
            node.NodePath, args, token)).ConfigureAwait(false);
    }

    /// <summary>验证 PATH 上全局 dsh 版本可解析（<c>dsh --version</c>）。</summary>
    private static async Task<string?> VerifyDshAsync(
        RuntimeBootstrapOptions options, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        (int exit, string? stdout, string? _) = await WithStepTimeoutAsync(options.StepTimeoutMinutes, ct,
            token => hooks.RunProcessAsync("dsh", ["--version"], token)).ConfigureAwait(false);
        return exit == 0 && RuntimeVersionGate.TryParseVersionOutput(stdout ?? string.Empty) is { } v ? v : null;
    }

    /// <summary>判定 npm/lockfile 是否因权限不足（需 sudo）失败。</summary>
    private static bool IsPermissionError(string? stdout, string? stderr)
    {
        string combined = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
        return combined.Contains("EACCES", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("EPERM", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>最佳努力清理目录（不存在即返回；失败不掩盖主流程结果）。</summary>
    private static void CleanupDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响引导结果（临时目录可能残留，但不影响系统全局已就位的 node）
        }
    }
}
