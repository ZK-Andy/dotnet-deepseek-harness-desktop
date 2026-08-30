namespace DeepSeek.Harness.Desktop.Services;

/// <summary>引导进度步骤。</summary>
public enum BootstrapStep
{
    /// <summary>确保系统全局 Node（PATH）可用；无则下载装到系统全局位。</summary>
    EnsureNode,

    /// <summary>经全局 npm 把 dsh 装到系统全局位（装 / 更新到 @alpha）。</summary>
    InstallDsh,

    /// <summary>验证 <c>dsh --version</c> 可解析（全局 dsh 就位）。</summary>
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
/// <param name="DshVersion">验证通过的全局 dsh 版本（Success=true 时非空；见 ADR simple-shell-single-global-dsh）。</param>
public sealed record BootstrapOutcome(
    bool Success,
    BootstrapStep Step,
    string? Error,
    string? DshVersion);

/// <summary>
/// 引导外部世界注入点：下载、取文本、解压、跑子进程、探测全局 node。生产实现见
/// <see cref="RuntimeBootstrap.CreateDefaultHooks"/>；测试注入 fakes（网络/文件系统边界，
/// 对齐 OrphanDshReaper.Reap 的委托注入风格）。
/// </summary>
public sealed record RuntimeBootstrapHooks(
    Func<string, string, CancellationToken, Task> DownloadFileAsync,
    Func<string, CancellationToken, Task<string>> FetchTextAsync,
    Func<string, string, CancellationToken, Task> ExtractArchiveAsync,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Stdout, string Stderr)>> RunProcessAsync,
    Func<CancellationToken, Task<(string? NodePath, string? NpmCli)>> ProbeLocalNodeAsync);

/// <summary>引导就位的系统全局 node 结果。</summary>
/// <param name="NodePath">node 可执行。</param>
/// <param name="NpmCli">npm-cli.js 路径。</param>
public sealed record NodeResult(string NodePath, string NpmCli);

/// <summary>
/// <see cref="RuntimeBootstrap"/> 的纯函数/文件系统原子面（partial，ADR 尺寸健康闸）。
/// 无网络/子进程调用，可独立单测；状态机（RunAsync/EnsureGlobalNodeAsync/InstallGlobalNodeAsync）在
/// <c>RuntimeBootstrap.cs</c>。
/// </summary>
public static partial class RuntimeBootstrap
{
    /// <summary>Node 发行包文件名坐标（纯函数可单测）：平台 RID → (归档文件名, 压缩格式)。</summary>
    /// <returns>文件名如 node-v24.20.0-linux-x64.tar.xz；不支持的平台返回 null（fail loud）。</returns>
    internal static string? NodeArchiveFileName(string nodeVersion)
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

    /// <summary>npm-cli.js 相对 Node 发行包根目录的路径（Node 发行包内布局随平台不同）。</summary>
    internal static string NpmCliRelativePath() => OperatingSystem.IsWindows()
        ? Path.Combine("node_modules", "npm", "bin", "npm-cli.js")
        : Path.Combine("lib", "node_modules", "npm", "bin", "npm-cli.js");

    /// <summary>node 最新版本解析地址（<see cref="RuntimeBootstrapOptions.NodeDistBaseUrl"/> 下 index.json）。</summary>
    internal static string LatestNodeIndexUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/index.json";

    /// <summary>从 nodejs.org index.json（按版本从新到旧排序）解析最新版（剥前导 v）。纯函数可单测。</summary>
    internal static string? ParseLatestNodeVersion(string indexJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(indexJson);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        foreach (System.Text.Json.JsonElement entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("version", out System.Text.Json.JsonElement v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string version = v.GetString()!.TrimStart('v', 'V');
                if (version.Length > 0)
                {
                    return version;
                }
            }
        }

        return null;
    }

    /// <summary>从 SHASUMS256.txt 内容提取指定文件名的 sha256（纯函数可单测）；无该行返回 null。</summary>
    internal static string? SelectSha256(string shasums, string fileName)
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
    internal static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using FileStream fs = File.OpenRead(path);
        byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return System.Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>跨平台剥 Win32 扩展长度路径前缀（<c>\\?\</c>/<c>\\?\UNC\</c>）——传给子进程的路径
    /// 带此前缀会破坏下游 shim/脚本解析（竞品 #198 实证）。</summary>
    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>node 全局 bin 目录（unix <c>&lt;prefix&gt;/bin</c>、Windows <c>&lt;prefix&gt;</c>）——
    /// node 与该 node 全局安装的 dsh 都落此目录；把它暴露到 PATH 即可同时解析 node 与 dsh（系统全局位）。</summary>
    internal static string NodeBinDir(string prefix) => OperatingSystem.IsWindows()
        ? prefix
        : Path.Combine(prefix, "bin");

    /// <summary>系统全局 node 默认安装前缀（用户可写，避免需 sudo）：Windows <c>%LOCALAPPDATA%\nodejs</c>；
    /// Unix 用户主目录 <c>~/.local</c>（<c>~/.local/bin</c> 经宿主 spawn PATH 增补与 CLI shim rc 块已在 PATH）。</summary>
    internal static string DefaultGlobalNodePrefix() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nodejs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local");

    /// <summary>哈希串缩略展示（防御性：可疑短串原样呈现，绝不越界切片）。</summary>
    private static string ShortHash(string hash) => hash.Length <= 16 ? hash : hash[..16] + "…";

    /// <summary>多源回落下载：按候选源序经 <see cref="RuntimeBootstrapHooks.DownloadFileAsync"/> 尝试，
    /// 上一源失败（保持 <paramref name="dest"/> 现有字节）即切下一源，全部失败聚合抛错。
    /// 仅吞真实源失败（网络/IO）；步超时（OCE）与编程 bug 不上抛让调用方 fail loud。</summary>
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
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException)
            {
                failures.Add($"{url}：{ex.Message}");
            }
        }

        throw new InvalidOperationException($"Node 发行包下载：所有候选源均失败（{string.Join("；", failures)}）");
    }

    /// <summary>断点续传下载：dest 已有字节则带 Range 请求；206 追加、200/416 重头写——正确性由后续 SHA256 校验兜底。</summary>
    internal static async Task DownloadResumableAsync(System.Net.Http.HttpClient http, string url, string dest, CancellationToken ct)
    {
        long existing = File.Exists(dest) ? new FileInfo(dest).Length : 0L;
        if (existing > 0)
        {
            using var rangeReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            rangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using System.Net.Http.HttpResponseMessage rangeResp = await http.SendAsync(rangeReq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (rangeResp.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                await CopyResponseBodyAsync(rangeResp, dest, append: true, ct).ConfigureAwait(false);
                return;
            }
        }

        using var fullReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        using System.Net.Http.HttpResponseMessage fullResp = await http.SendAsync(fullReq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        fullResp.EnsureSuccessStatusCode();
        await CopyResponseBodyAsync(fullResp, dest, append: false, ct).ConfigureAwait(false);
    }

    private static async Task CopyResponseBodyAsync(System.Net.Http.HttpResponseMessage resp, string dest, bool append, CancellationToken ct)
    {
        await using System.IO.Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(dest, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    /// <summary>递归复制目录内容到目标（目标已存在则覆盖/合并）——用于把 Node 发行包 bin/lib 落进系统全局前缀。</summary>
    internal static void CopyDirectoryRecursive(string src, string dst)
    {
        if (!Directory.Exists(src))
        {
            return;
        }

        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(src))
        {
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.EnumerateDirectories(src))
        {
            CopyDirectoryRecursive(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}
