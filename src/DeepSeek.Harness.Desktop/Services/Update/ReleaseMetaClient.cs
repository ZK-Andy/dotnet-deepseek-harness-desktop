using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>一次检查的解析产物：目标版本、匹配当前平台的资产与校验文件地址。</summary>
/// <param name="Version">release tag 版本（如 <c>v0.1.21</c>）。</param>
/// <param name="AssetName">资产文件名（如 <c>..._linux-amd64.deb</c>）。</param>
/// <param name="AssetUrl">资产下载绝对 URL。</param>
/// <param name="Sha256Url">SHA256SUMS.txt 绝对 URL；缺失时下载后跳过强校验（记日志）。</param>
public sealed record ReleaseMeta(string Version, string AssetName, string AssetUrl, string? Sha256Url)
{
    /// <summary>各 RID+包类型的资产文件名后缀（发布命名契约，见 release.yml/package 脚本；rpm 架构名与 deb 不同）。</summary>
    private static readonly Dictionary<string, string> RidKindSuffixes = new(StringComparer.Ordinal)
    {
        ["linux-x64:deb"] = "_linux-amd64.deb",
        ["linux-x64:rpm"] = "_linux-x86_64.rpm",
        ["linux-arm64:deb"] = "_linux-arm64.deb",
        ["linux-arm64:rpm"] = "_linux-aarch64.rpm",
        ["win-x64"] = "_windows-x64-setup.exe",
        ["osx-x64"] = "_macos-x64.dmg",
        ["osx-arm64"] = "_macos-arm64.dmg",
    };

    /// <summary>从 expanded_assets 页面的相对 href 集合里挑出当前 RID 的资产与 SHA256SUMS（纯函数，可单测）。</summary>
    /// <param name="pkgKind">Linux 包类型（<c>deb</c>/<c>rpm</c>，由宿主按系统包管理器检测）；win/mac 忽略。</param>
    /// <returns>解析失败（无匹配资产）返回 null，由调用方转 Error。</returns>
    public static ReleaseMeta? Pick(string version, IEnumerable<string> hrefs, string rid, string repository, string? pkgKind = null)
    {
        var suffixKey = rid.StartsWith("linux", StringComparison.Ordinal) ? $"{rid}:{pkgKind}" : rid;
        if (!RidKindSuffixes.TryGetValue(suffixKey, out var suffix))
        {
            return null;
        }

        const string downloadSegment = "/releases/download/";
        string? asset = null;
        string? sha = null;
        foreach (var raw in hrefs)
        {
            if (!raw.Contains(downloadSegment, StringComparison.Ordinal))
            {
                continue;
            }

            // 页面 href 是相对路径（/owner/repo/releases/download/...），归一化为绝对 URL
            var href = raw.StartsWith("/", StringComparison.Ordinal) ? $"https://github.com{raw}" : raw;
            if (asset is null && href.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                asset = href;
            }

            if (sha is null && href.EndsWith("/SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sha = href;
            }
        }

        return asset is null ? null : new ReleaseMeta(version, asset[(asset.LastIndexOf('/') + 1)..], asset, sha);
    }
}

/// <summary>
/// GitHub release 元数据客户端：<c>releases.atom</code> 取最新稳定 tag + <c>expanded_assets/&lt;tag&gt;</c> 页抓资产链接，
/// 绕开 api.github.com 60 次/h 限流（hairyf 同款路线）。
/// </summary>
public sealed partial class ReleaseMetaClient
{
    private readonly HttpClient _http;
    private readonly UpdateOptions _options;

    /// <summary>创建客户端。HttpClient 由外部持有复用连接池。</summary>
    public ReleaseMetaClient(HttpClient http, UpdateOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>抓取最新可升级版本的元数据；无任何稳定 release 或资产不齐返回 null。</summary>
    public async Task<ReleaseMeta?> FetchLatestAsync(string rid, string? pkgKind, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.FeedTimeoutSeconds));
        var version = await FetchLatestStableTagAsync(cts.Token).ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        var html = await _http.GetStringAsync(ExpandedAssetsUrl(version), cts.Token).ConfigureAwait(false);
        var hrefs = AssetHrefRegex().Matches(html).Select(m => m.Groups["href"].Value).ToList();
        return ReleaseMeta.Pick(version, hrefs, rid, _options.Repository, pkgKind);
    }

    /// <summary>atom 里按出现顺序取第一个稳定版 tag（不含 <c>-</c> 预发布段）；全是预发布则回退首个。</summary>
    public async Task<string?> FetchLatestStableTagAsync(CancellationToken cancellationToken)
    {
        var atom = await _http.GetStringAsync($"https://github.com/{_options.Repository}/releases.atom", cancellationToken).ConfigureAwait(false);
        var tags = TagRegex().Matches(atom).Select(m => m.Groups["tag"].Value).Distinct().ToList();
        if (tags.Count == 0)
        {
            return null;
        }

        foreach (var tag in tags)
        {
            if (!tag.Contains('-'))
            {
                return tag;
            }
        }

        return tags[0];
    }

    private string ExpandedAssetsUrl(string tag) =>
        $"https://github.com/{_options.Repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";

    /// <summary>expanded_assets 页里的下载链接（相对路径，指向 releases/download/）。</summary>
    [GeneratedRegex(@"href=""(?<href>/[^""]*releases/download/[^""]+)""")]
    private static partial Regex AssetHrefRegex();

    /// <summary>atom 条目 id 尾段的 tag 名（形如 <c>tag:github.com,2008:Repository/123/v0.1.20</c>）。</summary>
    [GeneratedRegex(@"<id>[^<]*/(?<tag>v?\d[\w.\-]*)</id>")]
    private static partial Regex TagRegex();
}
