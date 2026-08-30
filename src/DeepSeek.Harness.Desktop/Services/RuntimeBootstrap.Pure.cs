using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

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
/// <see cref="RuntimeBootstrap"/> 的纯函数/文件系统原子面（partial，ADR 尺寸健康闸）。
/// 无网络/npm/子进程调用，可独立单测；状态机（RunAsync/DownloadNodeAsync）在
/// <c>RuntimeBootstrap.cs</c>。
/// </summary>
public static partial class RuntimeBootstrap
{
    /// <summary>Node 发行包文件名坐标（纯函数可单测）：平台 RID → (归档文件名, 压缩格式)。</summary>
    /// <returns>文件名如 node-v24.20.0-linux-x64.tar.xz；不支持的平台返回 null（fail loud）。</returns>
    public static string? NodeArchiveFileName(string nodeVersion)
    {
        string? arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            _ => null,
        };
        if (arch is null)
        {
            return null;
        }

        string? platform = OperatingSystem.IsLinux() ? $"linux-{arch}"
            : OperatingSystem.IsMacOS() ? $"darwin-{arch}"
            : OperatingSystem.IsWindows() ? $"win-{arch}"
            : null;
        string ext = OperatingSystem.IsWindows() ? "zip" : OperatingSystem.IsMacOS() ? "tar.gz" : "tar.xz";
        return platform is null ? null : $"node-v{nodeVersion}-{platform}.{ext}";
    }

    /// <summary>npm-cli.js 相对运行时目录的路径（Node 发行包内布局随平台不同）。</summary>
    public static string NpmCliRelativePath() => OperatingSystem.IsWindows()
        ? Path.Combine("node_modules", "npm", "bin", "npm-cli.js")
        : Path.Combine("lib", "node_modules", "npm", "bin", "npm-cli.js");

    /// <summary>从 SHASUMS256.txt 内容提取指定文件名的 sha256（纯函数可单测）；无该行返回 null。</summary>
    public static string? SelectSha256(string shasums, string fileName)
    {
        foreach (string line in shasums.Split('\n'))
        {
            string[] parts = line.Trim().Split("  ", 2);
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
        await using FileStream fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    /// <summary>多源回落下载：按候选源序经 <see cref="RuntimeBootstrapHooks.DownloadFileAsync"/> 尝试，
    /// 上一源失败（保持 <paramref name="dest"/> 现有字节）即切下一源续传，全部失败聚合抛错。
    /// 仅吞真实源失败（网络/IO）；步超时（OCE）与编程 bug 不上抛让调用方 fail loud。
    /// 镜像仅在「已有可信摘要」后才进入候选（由调用方在摘要获取后构造），防投毒。</summary>
    internal static async Task DownloadWithFallbackAsync(
        IReadOnlyList<string> urls, string dest, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (string url in urls)
        {
            try
            {
                await hooks.DownloadFileAsync(url, dest, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // 仅把真实源失败（网络/IO）记为「该源不可用」，保留 dest 现有字节，下一源经 Range 断点续传；
                // 步超时（OCE）与编程 bug（如 NRE/InvalidOperationException）不在此吞，向上 fail loud（R2 B2）
                failures.Add($"{url}：{ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Node 发行包下载：所有候选源均失败（{string.Join("；", failures)}）");
    }

    /// <summary>原子 staging → runtimeDir 切换：runtimeDir 已存在则先移为 .backup，staging 再 swap 进，
    /// 成功后清理 backup；failed staging 移入时回滚 backup（有界锁重试，fail loud）。</summary>
    internal static void SwapStagingIntoPlace(string stagingDir, string runtimeDir)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(runtimeDir))!;
        if (!Directory.Exists(runtimeDir))
        {
            MoveWithLockRetry(stagingDir, runtimeDir);
            return;
        }

        string backup = Path.Combine(parent, $".backup-{Guid.NewGuid():N}");
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

    /// <summary>清扫跨崩溃残留的瞬时目录（<c>.staging-*</c>/<c>.backup-*</c>），最佳努力、非致命
    /// （R2 S1）。只处理由本引导下载路径生成的点前缀瞬时目录，绝不触碰用户/正式目录。</summary>
    internal static void CleanupStaleStagingDirs(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        foreach (string dir in Directory.EnumerateDirectories(parent))
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith(".staging-", StringComparison.Ordinal) ||
                name.StartsWith(".backup-", StringComparison.Ordinal))
            {
                try
                {
                    DeleteWithLockRetry(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 最佳努力：残留目录不阻塞本次引导
                }
            }
        }
    }

    /// <summary>生产断点续传下载：dest 已有字节则带 Range 请求；206 追加、200（服务端不支持 Range/
    /// 文件已变）重头写、416（Range 不可满足）重头兜底——正确性由后续 SHA256 校验兜底。</summary>
    internal static async Task DownloadResumableAsync(HttpClient http, string url, string dest, CancellationToken ct)
    {
        long existing = File.Exists(dest) ? new FileInfo(dest).Length : 0L;
        if (existing > 0)
        {
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, url);
            rangeReq.Headers.Range = new RangeHeaderValue(existing, null);
            using HttpResponseMessage rangeResp = await http.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (rangeResp.StatusCode == HttpStatusCode.PartialContent)
            {
                await CopyResponseBodyAsync(rangeResp, dest, append: true, ct).ConfigureAwait(false);
                return;
            }
            // 200（无 Range 支持/文件已变）或 416（Range 不可满足）：重头下载覆盖，绝不追加错位字节
        }

        using var fullReq = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage fullResp = await http.SendAsync(fullReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        fullResp.EnsureSuccessStatusCode();
        await CopyResponseBodyAsync(fullResp, dest, append: false, ct).ConfigureAwait(false);
    }

    /// <summary>把响应体写入 dest（append=true 追加、false 覆盖创建）。</summary>
    private static async Task CopyResponseBodyAsync(HttpResponseMessage resp, string dest, bool append, CancellationToken ct)
    {
        await using Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(dest, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    /// <summary>Windows 文件锁重试次数与间隔（AV/文件扫描器瞬时锁；达上限 fail loud）。</summary>
    internal const int FileLockRetryCount = 10;

    /// <summary>文件锁重试间隔。</summary>
    private static readonly TimeSpan s_fileLockRetryDelay = TimeSpan.FromMilliseconds(200);

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
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < FileLockRetryCount)
            {
                Thread.Sleep(s_fileLockRetryDelay);
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

        string dst = Path.Combine(targetDir, fileName);
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
