using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// GitHub release 元数据客户端：<c>releases.atom</code> 取最新稳定 tag + <c>expanded_assets/&lt;tag&gt;</c> 页抓资产链接，
/// 绕开 api.github.com 60 次/h 限流（hairyf 同款路线）。
/// </summary>
public sealed partial class ReleaseMetaClient
{
    private readonly HttpClient _http;
    private readonly UpdateOptions _options;
    private readonly Action<string>? _log;

    /// <summary>创建客户端。HttpClient 由外部持有复用连接池；<paramref name="log"/> 可选注入（宿主接 HostLog）。</summary>
    public ReleaseMetaClient(HttpClient http, UpdateOptions options, Action<string>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    /// <summary>抓取最新可升级版本的元数据；无任何稳定 release 或资产不齐返回 null。</summary>
    public async Task<ReleaseMeta?> FetchLatestAsync(string rid, string? pkgKind, CancellationToken cancellationToken)
    {
        // 检查链路起点与终点留痕：状态机的 Checking/Error 态给出结论，此处的耗时/无结果
        // 细节专供「检查挂起/超时/无 release」类实机现象的定位（无日志时无法区分网络黑洞
        // 与解析失败）
        long startedAt = Environment.TickCount64;
        _log?.Invoke($"[update] 检查：{_options.Repository}（rid={rid} kind={pkgKind ?? "n/a"} feed 超时={_options.FeedTimeoutSeconds}s）");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.FeedTimeoutSeconds));
        string? version = await FetchLatestStableTagAsync(cts.Token).ConfigureAwait(false);
        if (version is null)
        {
            _log?.Invoke($"[update] 检查完成：无稳定 release（{Environment.TickCount64 - startedAt}ms）");
            return null;
        }

        string html = await _http.GetStringAsync(ExpandedAssetsUrl(version), cts.Token).ConfigureAwait(false);
        var hrefs = AssetHrefRegex().Matches(html).Select(m => m.Groups["href"].Value).ToList();
        var meta = ReleaseMeta.Pick(version, hrefs, rid, _options.Repository, pkgKind);
        _log?.Invoke(
            meta is not null
                ? $"[update] 检查完成：{version} → {meta.AssetName}（{Environment.TickCount64 - startedAt}ms）"
                : $"[update] 检查完成：{version} 但无匹配资产（{Environment.TickCount64 - startedAt}ms）");
        return meta;
    }

    /// <summary>atom 里按出现顺序取第一个稳定版 tag（不含 <c>-</c> 预发布段）；全是预发布则回退首个。</summary>
    public async Task<string?> FetchLatestStableTagAsync(CancellationToken cancellationToken)
    {
        string atom = await _http.GetStringAsync($"https://github.com/{_options.Repository}/releases.atom", cancellationToken).ConfigureAwait(false);
        var tags = TagRegex().Matches(atom).Select(m => m.Groups["tag"].Value).Distinct().ToList();
        if (tags.Count == 0)
        {
            return null;
        }

        foreach (string? tag in tags)
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
