namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 首启引导状态机（ADR online-first-unbundled-runtime）：无捆绑运行时且无 PATH dsh 时，
/// 探测/复用本机 node → 下载钉版 Node（SHA256 校验）→ npm 安装钉版 dsh（当前 alpha.2）→ 验证产物入口。
/// 单次尝试语义：失败返回 <see cref="BootstrapOutcome"/> 非成功，重试循环由调用方驱动
/// （重试信号来自引导页的 desktop.bootstrap.retry 命令）。每步完成即验证产物——
/// 对齐竞品踩坑约束（readiness 竞态），不做 fire-and-forget。
/// 引擎辅助（进程捕获/探测/超时）与纯函数/文件系统原子面分别在
/// <c>RuntimeBootstrap.Engine.cs</c>/<c>RuntimeBootstrap.Pure.cs</c>（partial）。
/// </summary>
public static partial class RuntimeBootstrap
{
    /// <summary>生产 hooks：HttpClient 下载（断点续传 Range）/取文本、tar 解压（Linux/mac 原生 tar、
    /// Windows bsdtar 均在 PATH）、子进程直跑、PATH node 探测。路径统一剥 Win32 扩展前缀（ADR 踩坑约束 #198）。</summary>
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

    /// <summary>执行一次引导尝试。</summary>
    /// <param name="options">引导参数（钉版等）。</param>
    /// <param name="runtimeDir">运行时落位目录（<see cref="RuntimeLocator.ResolveDownloadedRuntimeDirectory"/>）。</param>
    /// <param name="report">进度出口（进度页 + host.log）。</param>
    /// <param name="hooks">外部世界注入点。</param>
    /// <param name="ct">取消令牌（应用退出）。</param>
    public static async Task<BootstrapOutcome> RunAsync(
        RuntimeBootstrapOptions options,
        string runtimeDir,
        Action<BootstrapProgress> report,
        RuntimeBootstrapHooks hooks,
        CancellationToken ct)
    {
        // 每步超时（StepTimeoutMinutes，R2 评审 B3：下载无超时则服务器停滞时无限 spinner 无出路）。
        // 步超时转 InvalidOperationException 走失败重试页；应用退出（parent ct）仍是 OCE 上抛
        BootstrapStep step = BootstrapStep.EnsureNode;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(runtimeDir))!);

            // ① Node 就位（EnsureNodePhaseAsync：已下载/本机复用/下载钉版）
            (string? nodePath, string? npmCli) = await EnsureNodePhaseAsync(options, runtimeDir, report, hooks, ct).ConfigureAwait(false);

            // ② npm 安装 dsh（registry）
            if (nodePath is null || npmCli is null)
            {
                return Fail(BootstrapStep.EnsureNode, "Node/npm 入口未解析（内部不一致，fail loud）");
            }

            // 复用本机 node 路径下 runtimeDir 可能尚未创建（下载路径的原子 swap 已就位）；npm install
            // 以 runtimeDir 为 prefix 必须保证其存在
            Directory.CreateDirectory(runtimeDir);

            // --loglevel=error 控制输出体积（大依赖树的进度刷屏会被 ReadToEnd 全量缓冲）。
            // npm 前置条件：runtimeDir 必须有 package.json——npm 对无清单目录的 `npm install <pkg>`
            // 是静默 no-op（exit 0 什么都不装，沙箱实证），先落最小清单
            string packageJson = Path.Combine(runtimeDir, "package.json");
            if (!File.Exists(packageJson))
            {
                File.WriteAllText(packageJson, "{\n  \"name\": \"dsh-desktop-runtime\",\n  \"private\": true\n}\n");
            }

            step = BootstrapStep.InstallDsh;
            report(new BootstrapProgress(BootstrapStep.InstallDsh, $"安装 dsh（{options.DshSpec}）"));
            (int exit, string? stdout, string? stderr) = await WithStepTimeoutAsync(options.StepTimeoutMinutes, ct, token => hooks.RunProcessAsync(
                nodePath,
                [npmCli!, "install", "--prefix", StripExtendedPrefix(runtimeDir), "--no-audit", "--no-fund", "--loglevel=error", options.DshSpec],
                token)).ConfigureAwait(false);
            if (exit != 0)
            {
                return Fail(BootstrapStep.InstallDsh, $"npm install 失败 exit={exit}：{(stderr.Length > 0 ? stderr : stdout).Trim()}");
            }

            // ③ 验证产物入口（对齐 ADR「每步装完验证产物」约束）
            step = BootstrapStep.VerifyDsh;
            report(new BootstrapProgress(BootstrapStep.VerifyDsh, "验证 dsh 产物"));
            (string NodeExe, string DshEntry)? located = RuntimeLocator.TryLocateBundled(runtimeDir);
            if (located is null)
            {
                return Fail(BootstrapStep.VerifyDsh, $"安装后未找到 dsh 入口（{runtimeDir}）");
            }

            (int vExit, string? vOut, string? vErr) = await WithStepTimeoutAsync(options.StepTimeoutMinutes, ct, token => hooks.RunProcessAsync(
                located.Value.NodeExe, [located.Value.DshEntry, "--version"], token)).ConfigureAwait(false);
            string? version = RuntimeVersionGate.TryParseVersionOutput(vOut);
            if (vExit != 0 || version is null)
            {
                return Fail(BootstrapStep.VerifyDsh, $"dsh --version 验证失败 exit={vExit}：{vErr.Trim()}");
            }

            report(new BootstrapProgress(BootstrapStep.Ready, $"dsh {version} 就绪"));
            return new BootstrapOutcome(true, BootstrapStep.Ready, null, located);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 引导是长链路网络/文件操作，任何一步的意外异常都收口为人可读失败态（进度页可重试），
            // 绝不让壳崩——真正的启动失败由后续 StartAsync 的降级链路呈现。step 变量随进度推进，
            // 失败归属步骤用于进度页红色高亮
            return Fail(step, ex.Message);
        }
    }

    private static async Task<(string NodePath, string NpmCli)> DownloadNodeAsync(
        RuntimeBootstrapOptions options,
        string runtimeDir,
        Action<BootstrapProgress> report,
        RuntimeBootstrapHooks hooks,
        CancellationToken ct)
    {
        string fileName = NodeArchiveFileName(options.NodeVersion)
            ?? throw new InvalidOperationException("当前平台无对应的 Node 发行包坐标（fail loud）");
        string baseUrl = options.NodeDistBaseUrl.TrimEnd('/');
        string versionDir = $"v{options.NodeVersion}";
        string parent = Path.GetDirectoryName(Path.GetFullPath(runtimeDir))!;

        // 跨崩溃残留：swap 两处 Directory.Move 之间崩溃会留下 .backup-<guid> 旧运行时、解压中断留 .staging-<guid>；
        // 启动下载前最佳努力清扫，避免逐次泄漏（R2 S1）。仅清扫本运行时父目录下的这类瞬时目录
        CleanupStaleStagingDirs(parent);

        // 摘要优先取自官方（信任根）：先拉 SHASUMS256.txt 得可信摘要，随后归档（官方→镜像）逐一用它校验；
        // 官方摘要不可达即中止（无可信摘要 → 不用镜像，防投毒，ADR 批次三）
        string expected = await FetchSha256ExpectedAsync(options, baseUrl, versionDir, fileName, report, hooks, ct).ConfigureAwait(false);

        // 归档落确定性命名的 .download 区（runtimeDir 同盘兄弟，跨重试/跨进程续传）；解压与归一落
        // .staging 临时目录，成功再原子 swap 进 runtimeDir。三者与 runtimeDir 同卷（跨卷 Directory.Move
        // 会 Invalid cross-device link，/tmp=tmpfs 的 Linux 发行版上首启必炸）
        string downloadDir = Path.Combine(parent, $".download-{Path.GetFileName(Path.GetFullPath(runtimeDir))}", versionDir);
        Directory.CreateDirectory(downloadDir);
        string archivePath = Path.Combine(downloadDir, fileName);
        var candidates = new List<string> { $"{baseUrl}/{versionDir}/{fileName}" };
        if (!string.IsNullOrWhiteSpace(options.NodeMirrorBaseUrl))
        {
            candidates.Add($"{options.NodeMirrorBaseUrl.TrimEnd('/')}/{versionDir}/{fileName}");
        }

        string stagingDir = Path.Combine(parent, $".staging-{Guid.NewGuid():N}");
        try
        {
            report(new BootstrapProgress(BootstrapStep.EnsureNode, $"下载 Node v{options.NodeVersion}"));
            await WithStepTimeout(options.StepTimeoutMinutes, ct,
                token => DownloadWithFallbackAsync(candidates, archivePath, hooks, token)).ConfigureAwait(false);

            // SHA256 校验：摘要来自官方 SHASUMS256.txt（HTTPS），镜像内容同源由此兜底；缺失摘要属
            // 供应链异常，安全中止。威胁模型边界（ADR）：该校验只防传输损坏，非信任锚（base url 可配置时同源自证）
            report(new BootstrapProgress(BootstrapStep.ExtractNode, "校验 SHA256"));
            string actual = await Sha256FileAsync(archivePath, ct).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Node 发行包 SHA256 不匹配（期望 {ShortHash(expected)}，实际 {ShortHash(actual)}），安全中止");
            }

            // 解压 → 归一扁平布局 → 原子 swap 进 runtimeDir（ExtractNormalizeAndSwapAsync）
            await ExtractNormalizeAndSwapAsync(options, stagingDir, runtimeDir, archivePath, report, hooks, ct).ConfigureAwait(false);
        }
        finally
        {
            // staging 成功即被 swap 移走；失败清理其解压/归一产物（.download 保留供续传，见下）
            CleanupDirectory(stagingDir);
        }

        // 原子就位完成：清理 .download 归档区（含历史半成品，已无续传价值）；Windows 锁重试
        CleanupDirectory(downloadDir);

        string nodePath = TryFindNode(runtimeDir)
            ?? throw new InvalidOperationException("归一后未找到 node 可执行（布局异常）");
        string npmCli = Path.Combine(runtimeDir, NpmCliRelativePath());
        if (!File.Exists(npmCli))
        {
            throw new InvalidOperationException("归一后未找到 npm-cli.js（布局异常）");
        }

        return (nodePath, npmCli);
    }
}
