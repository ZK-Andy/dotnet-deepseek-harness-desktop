using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>引导进度步骤。</summary>
public enum BootstrapStep
{
    /// <summary>探测本机运行时（PATH node/dsh）。</summary>
    ProbeLocal,

    /// <summary>获取 Node（本机复用或下载钉版发行包）。</summary>
    EnsureNode,

    /// <summary>校验下载产物（SHA256）并解压。</summary>
    ExtractNode,

    /// <summary>经 npm 安装 dsh（registry）。</summary>
    InstallDsh,

    /// <summary>验证产物入口（bin.js 可执行、版本可解析）。</summary>
    VerifyDsh,

    /// <summary>引导完成。</summary>
    Ready,
}

/// <summary>一条引导进度（推给进度页与 host.log）。</summary>
public sealed record BootstrapProgress(BootstrapStep Step, string Message, bool Failed = false);

/// <summary>一次引导尝试的结果。</summary>
/// <param name="Success">是否完成。</param>
/// <param name="Error">失败原因（Success=false 时非空，人可读）。</param>
/// <param name="Runtime">就位的运行时 (node 可执行, dsh bin.js)。</param>
public sealed record BootstrapOutcome(bool Success, string? Error, (string NodeExe, string DshEntry)? Runtime)
{
    /// <summary>用户放弃引导（关窗退出）的终态。</summary>
    public static readonly BootstrapOutcome Quit = new(false, null, null);
}

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

    /// <summary>生产 hooks：HttpClient 下载/取文本、tar 解压（Linux/mac 原生 tar、Windows bsdtar 均在 PATH）、
    /// 子进程直跑、PATH node 探测。路径统一剥 Win32 扩展前缀（ADR 踩坑约束 #198）。</summary>
    public static RuntimeBootstrapHooks CreateDefaultHooks(Action<string> log)
    {
        return new RuntimeBootstrapHooks(
            DownloadFileAsync: async (url, dest, ct) =>
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
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
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, stdout, stderr);
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
        try
        {
            Directory.CreateDirectory(runtimeDir);

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

            // --loglevel=error 控制输出体积（大依赖树的进度刷屏会被 ReadToEnd 全量缓冲）。
            // npm 前置条件：runtimeDir 必须有 package.json——npm 对无清单目录的 `npm install <pkg>`
            // 是静默 no-op（exit 0 什么都不装，沙箱实证），先落最小清单
            var packageJson = Path.Combine(runtimeDir, "package.json");
            if (!File.Exists(packageJson))
            {
                File.WriteAllText(packageJson, "{\n  \"name\": \"dsh-desktop-runtime\",\n  \"private\": true\n}\n");
            }

            report(new BootstrapProgress(BootstrapStep.InstallDsh, $"安装 dsh（{options.DshSpec}）"));
            var (exit, stdout, stderr) = await hooks.RunProcessAsync(
                nodePath,
                [npmCli!, "install", "--prefix", StripExtendedPrefix(runtimeDir), "--no-audit", "--no-fund", "--loglevel=error", options.DshSpec],
                ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return Fail(BootstrapStep.InstallDsh, $"npm install 失败 exit={exit}：{(stderr.Length > 0 ? stderr : stdout).Trim()}");
            }

            // ③ 验证产物入口（对齐 ADR「每步装完验证产物」约束）
            report(new BootstrapProgress(BootstrapStep.VerifyDsh, "验证 dsh 产物"));
            var located = RuntimeLocator.TryLocateBundled(runtimeDir);
            if (located is null)
            {
                return Fail(BootstrapStep.VerifyDsh, $"安装后未找到 dsh 入口（{runtimeDir}）");
            }

            var (vExit, vOut, vErr) = await hooks.RunProcessAsync(
                located.Value.NodeExe, [located.Value.DshEntry, "--version"], ct).ConfigureAwait(false);
            var version = RuntimeVersionGate.TryParseVersionOutput(vOut);
            if (vExit != 0 || version is null)
            {
                return Fail(BootstrapStep.VerifyDsh, $"dsh --version 验证失败 exit={vExit}：{vErr.Trim()}");
            }

        report(new BootstrapProgress(BootstrapStep.Ready, $"dsh {version} 就绪"));
        return new BootstrapOutcome(true, null, located);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 引导是长链路网络/文件操作，任何一步的意外异常都收口为人可读失败态（进度页可重试），
            // 绝不让壳崩——真正的启动失败由后续 StartAsync 的降级链路呈现
            return Fail(BootstrapStep.Ready, ex.Message);
        }
    }

    private static BootstrapOutcome Fail(BootstrapStep step, string error)
    {
        // 失败进度由调用方在重试循环推送（此处返回即可，避免双通道）
        return new BootstrapOutcome(false, $"[{step}] {error}", null);
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
        var archivePath = Path.Combine(Path.GetTempPath(), $"dsh-desktop-{fileName}");

        report(new BootstrapProgress(BootstrapStep.EnsureNode, $"下载 Node v{options.NodeVersion}"));
        await hooks.DownloadFileAsync($"{baseUrl}/{versionDir}/{fileName}", archivePath, ct).ConfigureAwait(false);

        // SHA256 校验：摘要与文件同源于官方 dist 目录（HTTPS），缺失摘要属供应链异常，安全中止
        report(new BootstrapProgress(BootstrapStep.ExtractNode, "校验 SHA256"));
        var shasums = await hooks.FetchTextAsync($"{baseUrl}/{versionDir}/SHASUMS256.txt", ct).ConfigureAwait(false);
        var expected = SelectSha256(shasums, fileName)
            ?? throw new InvalidOperationException($"SHASUMS256.txt 缺少 {fileName} 的摘要，安全中止");
        var actual = await Sha256FileAsync(archivePath, ct).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Node 发行包 SHA256 不匹配（期望 {ShortHash(expected)}，实际 {ShortHash(actual)}），安全中止");
        }

        report(new BootstrapProgress(BootstrapStep.ExtractNode, "解压 Node"));
        var extractDir = Path.Combine(Path.GetTempPath(), $"dsh-desktop-node-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            await hooks.ExtractArchiveAsync(archivePath, extractDir, ct).ConfigureAwait(false);

            // 归一为捆绑闭包同款扁平布局（RuntimeLocator.TryLocateBundled 的探测形态）：
            // node(.exe) 在根目录、npm 模块树按平台相对路径可达；发行包其余内容
            // （include/share/CHANGELOG 等）删除，保持 runtimeDir 与闭包同构。
            var inner = Directory.EnumerateDirectories(extractDir).FirstOrDefault()
                ?? throw new InvalidOperationException("Node 发行包解压结果无内容目录");
            var nodeRel = OperatingSystem.IsWindows() ? "node.exe" : Path.Combine("bin", "node");
            var npmTreeRel = OperatingSystem.IsWindows() ? "node_modules" : "lib";
            MoveInto(runtimeDir, Path.Combine(inner, nodeRel), Path.GetFileName(nodeRel));
            MoveInto(runtimeDir, Path.Combine(inner, npmTreeRel), npmTreeRel);
            foreach (var leftover in Directory.EnumerateFileSystemEntries(inner))
            {
                if (Directory.Exists(leftover))
                {
                    Directory.Delete(leftover, recursive: true);
                }
                else
                {
                    File.Delete(leftover);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(extractDir, recursive: true);
            }
            catch (IOException)
            {
                // 临时解压目录清理失败不影响引导结果（系统临时目录自清）
            }
        }

        var nodePath = TryFindNode(runtimeDir)
            ?? throw new InvalidOperationException("归一后未找到 node 可执行（布局异常）");
        var npmCli = Path.Combine(runtimeDir, NpmCliRelativePath());
        if (!File.Exists(npmCli))
        {
            throw new InvalidOperationException("归一后未找到 npm-cli.js（布局异常）");
        }

        return (nodePath, npmCli);
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
