using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>引导进度步骤。</summary>
public enum BootstrapStep
{
    /// <summary>获取 Node（本机复用或下载钉版发行包）。</summary>
    EnsureNode,

    /// <summary>校验下载产物（SHA256）并解压。</summary>
    ExtractNode,

    /// <summary>经 npm 安装 dsh（registry）。</summary>
    InstallDsh,

    /// <summary>验证产物入口（bin.js 可执行、版本可解析）。</summary>
    VerifyDsh,

    /// <summary>插件引导（ADR reference-alignment 批次二）：运行时就位后的可选插件确认/安装相。
    /// 仅作引导页步骤呈现——实际交互由 <c>dsh-desktop-preinstall</c> 事件驱动，
    /// RuntimeBootstrap 的步骤机不产出此值。</summary>
    PreinstallPlugins,

    /// <summary>引导完成。</summary>
    Ready,
}

/// <summary>一条引导进度（推给进度页与 host.log）。</summary>
public sealed record BootstrapProgress(BootstrapStep Step, string Message, bool Failed = false);

/// <summary>一次引导尝试的结果。</summary>
/// <param name="Success">是否完成。</param>
/// <param name="Step">结果所在步骤（失败时即失败步骤，供进度页高亮）。</param>
/// <param name="Error">失败原因（Success=false 时非空，人可读）。</param>
/// <param name="Runtime">就位的运行时 (node 可执行, dsh bin.js)。</param>
public sealed record BootstrapOutcome(
    bool Success,
    BootstrapStep Step,
    string? Error,
    (string NodeExe, string DshEntry)? Runtime);

/// <summary>
/// 引导外部世界注入点：下载、取文本、解压、跑子进程、探测本机 node。生产实现见
/// <see cref="RuntimeBootstrap.CreateDefaultHooks"/>；测试注入 fakes（网络/文件系统边界，
/// 对齐 OrphanDshReaper.Reap 的委托注入风格）。
/// </summary>
public sealed record RuntimeBootstrapHooks(
    Func<string, string, CancellationToken, Task> DownloadFileAsync,
    Func<string, CancellationToken, Task<string>> FetchTextAsync,
    Func<string, string, CancellationToken, Task> ExtractArchiveAsync,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Stdout, string Stderr)>> RunProcessAsync,
    Func<CancellationToken, Task<(string? NodePath, string? Version)>> ProbeLocalNodeAsync);

/// <summary>
/// 首启引导状态机（ADR online-first-unbundled-runtime）：无捆绑运行时且无 PATH dsh 时，
/// 探测/复用本机 node → 下载钉版 Node（SHA256 校验）→ npm 安装 dsh@latest → 验证产物入口。
/// 单次尝试语义：失败返回 <see cref="BootstrapOutcome"/> 非成功，重试循环由调用方驱动
/// （重试信号来自引导页的 desktop.bootstrap.retry 命令）。每步完成即验证产物——
/// 对齐竞品踩坑约束（readiness 竞态），不做 fire-and-forget。
/// </summary>
public static class RuntimeBootstrap
{
    /// <summary>Node 发行包文件名坐标（纯函数可单测）：平台 RID → (归档文件名, 压缩格式)。</summary>
    /// <returns>文件名如 node-v24.20.0-linux-x64.tar.xz；不支持的平台返回 null（fail loud）。</returns>
    public static string? NodeArchiveFileName(string nodeVersion)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            _ => null,
        };
        if (arch is null)
        {
            return null;
        }

        var platform = OperatingSystem.IsLinux() ? $"linux-{arch}"
            : OperatingSystem.IsMacOS() ? $"darwin-{arch}"
            : OperatingSystem.IsWindows() ? $"win-{arch}"
            : null;
        var ext = OperatingSystem.IsWindows() ? "zip" : OperatingSystem.IsMacOS() ? "tar.gz" : "tar.xz";
        return platform is null ? null : $"node-v{nodeVersion}-{platform}.{ext}";
    }

    /// <summary>npm-cli.js 相对运行时目录的路径（Node 发行包内布局随平台不同）。</summary>
    public static string NpmCliRelativePath() => OperatingSystem.IsWindows()
        ? Path.Combine("node_modules", "npm", "bin", "npm-cli.js")
        : Path.Combine("lib", "node_modules", "npm", "bin", "npm-cli.js");

    /// <summary>从 SHASUMS256.txt 内容提取指定文件名的 sha256（纯函数可单测）；无该行返回 null。</summary>
    public static string? SelectSha256(string shasums, string fileName)
    {
        foreach (var line in shasums.Split('\n'))
        {
            var parts = line.Trim().Split("  ", 2);
            if (parts.Length == 2 && parts[1] == fileName)
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>计算文件 SHA256（十六进制小写）。</summary>
    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

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
                var (exit, _, stderr) = await RunCaptureAsync(
                    log, "tar", ["-xf", StripExtendedPrefix(archive), "-C", StripExtendedPrefix(destDir)], ct).ConfigureAwait(false);
                if (exit != 0)
                {
                    throw new InvalidOperationException($"tar 解压失败 exit={exit}：{stderr.Trim()}");
                }
            },
            RunProcessAsync: (exe, args, ct) => RunCaptureAsync(log, exe, args, ct),
            ProbeLocalNodeAsync: ct => ProbeLocalNodeAsync(log, ct));
    }

    /// <summary>跨平台剥 Win32 扩展长度路径前缀（<c>\\?\</c>/<c>\\?\UNC\</c>）——传给子进程的路径
    /// 带此前缀会破坏下游 shim/脚本解析（竞品 #198 实证）。</summary>
    public static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>哈希串缩略展示（防御性：来源可疑的短串原样呈现，绝不越界切片）。</summary>
    private static string ShortHash(string hash) => hash.Length <= 16 ? hash : hash[..16] + "…";

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCaptureAsync(
        Action<string> log, string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = StripExtendedPrefix(exe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // GUI 子系统壳 spawn tar/node/npm 不能闪控制台窗（Windows）
            CreateNoWindow = true,
        };
        HarnessRuntimeHost.UseUtf8TextStreams(psi);
        foreach (var arg in args)
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
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程 {exe}");
        try
        {
            // 双流并发读：顺序先读 stdout 时 stderr 塞满 pipe buffer（~64KB）会互等死锁
            // （npm 崩溃 full report 可超限）。进程退出后两流必然收尾，收尾读取不再带 ct
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
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

    private static readonly Regex NodeVersionToken = new(
        @"^v(\d+)\.\d+\.\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static async Task<(string? NodePath, string? Version)> ProbeLocalNodeAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = await RunCaptureAsync(log, "node", ["--version"], ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return (null, null);
            }

            var m = NodeVersionToken.Match(stdout.Trim());
            return m.Success ? ("node", m.Groups[1].Value) : (null, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke($"[bootstrap] PATH node 探测失败（视为不存在）：{ex.Message}");
            return (null, null);
        }
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
        var step = BootstrapStep.EnsureNode;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(runtimeDir))!);

            // ① Node 就位：优先已下载（断点重试语义），再本机复用，最后下载钉版
            report(new BootstrapProgress(BootstrapStep.EnsureNode, "检测本机 Node"));
            var nodePath = TryFindNode(runtimeDir);
            string? npmCli = null;
            if (nodePath is not null)
            {
                report(new BootstrapProgress(BootstrapStep.EnsureNode, "复用已下载的 Node"));
                npmCli = Path.Combine(runtimeDir, NpmCliRelativePath());
            }
            else
            {
                var (localNode, localMajor) = await hooks.ProbeLocalNodeAsync(ct).ConfigureAwait(false);
                var reuse = localNode is not null &&
                            int.TryParse(localMajor, out var major) &&
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
            var packageJson = Path.Combine(runtimeDir, "package.json");
            if (!File.Exists(packageJson))
            {
                File.WriteAllText(packageJson, "{\n  \"name\": \"dsh-desktop-runtime\",\n  \"private\": true\n}\n");
            }

            step = BootstrapStep.InstallDsh;
            report(new BootstrapProgress(BootstrapStep.InstallDsh, $"安装 dsh（{options.DshSpec}）"));
            var (exit, stdout, stderr) = await WithStepTimeout(options.StepTimeoutMinutes, ct, token => hooks.RunProcessAsync(
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
            var located = RuntimeLocator.TryLocateBundled(runtimeDir);
            if (located is null)
            {
                return Fail(BootstrapStep.VerifyDsh, $"安装后未找到 dsh 入口（{runtimeDir}）");
            }

            var (vExit, vOut, vErr) = await WithStepTimeout(options.StepTimeoutMinutes, ct, token => hooks.RunProcessAsync(
                located.Value.NodeExe, [located.Value.DshEntry, "--version"], token)).ConfigureAwait(false);
            var version = RuntimeVersionGate.TryParseVersionOutput(vOut);
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
        var node = Path.Combine(runtimeDir, "node");
        if (File.Exists(node))
        {
            return node;
        }

        var nodeExe = Path.Combine(runtimeDir, "node.exe");
        return File.Exists(nodeExe) ? nodeExe : null;
    }

    private static string? LocateNpmCliBesideLocalNode(string nodePath)
    {
        // node 在 PATH 上时布局不定（发行包/包管理器/symlink）；只探测两种主流布局，
        // 找不到 npm-cli 一律视为不可复用——绝不猜测执行
        var dir = Path.GetDirectoryName(Path.GetFullPath(nodePath));
        if (dir is null)
        {
            return null;
        }

        var candidates = new[]
        {
            // 发行包布局：node 同级的 lib/node_modules（unix）或同级 node_modules（win）
            Path.Combine(dir, NpmCliRelativePath()),
            Path.Combine(dir, "..", NpmCliRelativePath()),
            Path.Combine(dir, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(string NodePath, string NpmCli)> DownloadNodeAsync(
        RuntimeBootstrapOptions options,
        string runtimeDir,
        Action<BootstrapProgress> report,
        RuntimeBootstrapHooks hooks,
        CancellationToken ct)
    {
        var fileName = NodeArchiveFileName(options.NodeVersion)
            ?? throw new InvalidOperationException("当前平台无对应的 Node 发行包坐标（fail loud）");
        var baseUrl = options.NodeDistBaseUrl.TrimEnd('/');
        var versionDir = $"v{options.NodeVersion}";
        var parent = Path.GetDirectoryName(Path.GetFullPath(runtimeDir))!;

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

        var expected = SelectSha256(shasums, fileName)
            ?? throw new InvalidOperationException($"SHASUMS256.txt 缺少 {fileName} 的摘要，安全中止");

        // 归档落确定性命名的 .download 区（runtimeDir 同盘兄弟，跨重试/跨进程续传）；解压与归一落
        // .staging 临时目录，成功再原子 swap 进 runtimeDir。三者与 runtimeDir 同卷（跨卷 Directory.Move
        // 会 Invalid cross-device link，/tmp=tmpfs 的 Linux 发行版上首启必炸）
        var downloadDir = Path.Combine(parent, $".download-{Path.GetFileName(Path.GetFullPath(runtimeDir))}", versionDir);
        Directory.CreateDirectory(downloadDir);
        var archivePath = Path.Combine(downloadDir, fileName);
        var candidates = new List<string> { $"{baseUrl}/{versionDir}/{fileName}" };
        if (!string.IsNullOrWhiteSpace(options.NodeMirrorBaseUrl))
        {
            candidates.Add($"{options.NodeMirrorBaseUrl.TrimEnd('/')}/{versionDir}/{fileName}");
        }

        var stagingDir = Path.Combine(parent, $".staging-{Guid.NewGuid():N}");
        try
        {
            report(new BootstrapProgress(BootstrapStep.EnsureNode, $"下载 Node v{options.NodeVersion}"));
            await WithStepTimeout(options.StepTimeoutMinutes, ct,
                token => DownloadWithFallbackAsync(candidates, archivePath, hooks, token)).ConfigureAwait(false);

            // SHA256 校验：摘要来自官方 SHASUMS256.txt（HTTPS），镜像内容同源由此兜底；缺失摘要属
            // 供应链异常，安全中止。威胁模型边界（ADR）：该校验只防传输损坏，非信任锚（base url 可配置时同源自证）
            report(new BootstrapProgress(BootstrapStep.ExtractNode, "校验 SHA256"));
            var actual = await Sha256FileAsync(archivePath, ct).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Node 发行包 SHA256 不匹配（期望 {ShortHash(expected)}，实际 {ShortHash(actual)}），安全中止");
            }

            report(new BootstrapProgress(BootstrapStep.ExtractNode, "解压 Node"));
            var extractDir = Path.Combine(stagingDir, "extract");
            Directory.CreateDirectory(extractDir);
            await WithStepTimeout(options.StepTimeoutMinutes, ct,
                token => hooks.ExtractArchiveAsync(archivePath, extractDir, token)).ConfigureAwait(false);

            // 归一为捆绑闭包同款扁平布局（RuntimeLocator.TryLocateBundled 的探测形态）于 staging 根：
            // node(.exe) 在根目录、npm 模块树按平台相对路径可达；发行包其余内容（include/share/
            // CHANGELOG 等）随 staging 清理，保持 runtimeDir 与闭包同构
            var inner = Directory.EnumerateDirectories(extractDir).FirstOrDefault()
                ?? throw new InvalidOperationException("Node 发行包解压结果无内容目录");
            var nodeRel = OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node");
            var npmTreeRel = OperatingSystem.IsWindows() ? "node_modules" : "lib";
            MoveInto(stagingDir, Path.Combine(inner, nodeRel), Path.GetFileName(nodeRel));
            MoveInto(stagingDir, Path.Combine(inner, npmTreeRel), npmTreeRel);

            report(new BootstrapProgress(BootstrapStep.ExtractNode, "原子就位运行时"));
            SwapStagingIntoPlace(stagingDir, runtimeDir);
        }
        finally
        {
            // staging 成功即被 swap 移走；失败清理其解压/归一产物（.download 保留供续传，见下）
            CleanupDirectory(stagingDir);
        }

        // 原子就位完成：清理 .download 归档区（含历史半成品，已无续传价值）；Windows 锁重试
        CleanupDirectory(downloadDir);

        var nodePath = TryFindNode(runtimeDir)
            ?? throw new InvalidOperationException("归一后未找到 node 可执行（布局异常）");
        var npmCli = Path.Combine(runtimeDir, NpmCliRelativePath());
        if (!File.Exists(npmCli))
        {
            throw new InvalidOperationException("归一后未找到 npm-cli.js（布局异常）");
        }

        return (nodePath, npmCli);
    }

    /// <summary>多源回落下载：按候选源序经 <see cref="RuntimeBootstrapHooks.DownloadFileAsync"/> 尝试，
    /// 上一源失败（保持 <paramref name="dest"/> 现有字节）即切下一源续传，全部失败聚合抛错。
    /// 镜像仅在「已有可信摘要」后才进入候选（由调用方在摘要获取后构造），防投毒。</summary>
    internal static async Task DownloadWithFallbackAsync(
        IReadOnlyList<string> urls, string dest, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await hooks.DownloadFileAsync(url, dest, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 上一源失败：保留 dest 现有字节，下一源经 Range 断点续传；步超时（OCE）不在此吞
                last = ex;
            }
        }

        throw new InvalidOperationException($"Node 发行包下载：所有候选源均失败（{last?.Message}）");
    }

    /// <summary>原子 staging → runtimeDir 切换：runtimeDir 已存在则先移为 .backup，staging 再 swap 进，
    /// 成功后清理 backup；failed staging 移入时回滚 backup（有界锁重试，fail loud）。</summary>
    internal static void SwapStagingIntoPlace(string stagingDir, string runtimeDir)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(runtimeDir))!;
        if (!Directory.Exists(runtimeDir))
        {
            MoveWithLockRetry(stagingDir, runtimeDir);
            return;
        }

        var backup = Path.Combine(parent, $".backup-{Guid.NewGuid():N}");
        MoveWithLockRetry(runtimeDir, backup);
        try
        {
            MoveWithLockRetry(stagingDir, runtimeDir);
        }
        catch
        {
            // 回滚：把 backup 移回 runtimeDir（staging 未占用该位置时），再上抛；恢复失败则留 backup 供排查
            if (Directory.Exists(backup) && !Directory.Exists(runtimeDir))
            {
                try
                {
                    MoveWithLockRetry(backup, runtimeDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // backup 仍在原处，下次引导重试会重新处理；这里不掩盖原始 swap 异常
                }
            }

            throw;
        }

        CleanupDirectory(backup);
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
            DeleteWithLockRetry(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响引导结果（半成品目录可能残留，但不影响 runtimeDir 已就位的运行时）
        }
    }

    /// <summary>生产断点续传下载：dest 已有字节则带 Range 请求；206 追加、200（服务端不支持 Range/
    /// 文件已变）重头写、416（Range 不可满足）重头兜底——正确性由后续 SHA256 校验兜底。</summary>
    internal static async Task DownloadResumableAsync(HttpClient http, string url, string dest, CancellationToken ct)
    {
        var existing = File.Exists(dest) ? new FileInfo(dest).Length : 0L;
        if (existing > 0)
        {
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, url);
            rangeReq.Headers.Range = new RangeHeaderValue(existing, null);
            using var rangeResp = await http.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (rangeResp.StatusCode == HttpStatusCode.PartialContent)
            {
                await CopyResponseBodyAsync(rangeResp, dest, append: true, ct).ConfigureAwait(false);
                return;
            }
            // 200（无 Range 支持/文件已变）或 416（Range 不可满足）：重头下载覆盖，绝不追加错位字节
        }

        using var fullReq = new HttpRequestMessage(HttpMethod.Get, url);
        using var fullResp = await http.SendAsync(fullReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        fullResp.EnsureSuccessStatusCode();
        await CopyResponseBodyAsync(fullResp, dest, append: false, ct).ConfigureAwait(false);
    }

    /// <summary>把响应体写入 dest（append=true 追加、false 覆盖创建）。</summary>
    private static async Task CopyResponseBodyAsync(HttpResponseMessage resp, string dest, bool append, CancellationToken ct)
    {
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(dest, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    /// <summary>Windows 文件锁重试次数与间隔（AV/文件扫描器瞬时锁；达上限 fail loud）。</summary>
    internal const int FileLockRetryCount = 10;

    /// <summary>文件锁重试间隔。</summary>
    private static readonly TimeSpan FileLockRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>带锁重试的目录/文件移动（rename），对 IO 瞬时锁有界重试。</summary>
    internal static void MoveWithLockRetry(string source, string dest)
    {
        WithLockRetry(() => Directory.Move(source, dest));
    }

    /// <summary>带锁重试的目录/文件删除（remove），对 IO 瞬时锁有界重试。</summary>
    internal static void DeleteWithLockRetry(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        WithLockRetry(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>有界重试执行器：仅对 <see cref="IOException"/> 瞬时锁重试，达 <see cref="FileLockRetryCount"/>
    /// 次后上抛（fail loud）。</summary>
    internal static void WithLockRetry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < FileLockRetryCount)
            {
                Thread.Sleep(FileLockRetryDelay);
            }
        }
    }

    /// <summary>把发行包内单个条目搬入目标目录（同盘 Directory.Move；目标已存在先清，幂等重入安全）。</summary>
    private static void MoveInto(string targetDir, string src, string fileName)
    {
        if (!File.Exists(src) && !Directory.Exists(src))
        {
            throw new InvalidOperationException($"Node 发行包缺少 {fileName}（布局异常）");
        }

        var dst = Path.Combine(targetDir, fileName);
        if (Directory.Exists(dst))
        {
            Directory.Delete(dst, recursive: true);
        }
        else if (File.Exists(dst))
        {
            File.Delete(dst);
        }

        Directory.Move(src, dst);
    }
}
