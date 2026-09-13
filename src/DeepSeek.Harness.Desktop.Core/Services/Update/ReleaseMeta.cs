namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>一次检查的解析产物：目标版本、匹配当前平台的资产与校验文件地址。</summary>
/// <param name="Version">release tag 版本（如 <c>v0.1.21</c>）。</param>
/// <param name="AssetName">资产文件名（如 <c>..._linux-amd64.deb</c>）。</param>
/// <param name="AssetUrl">资产下载绝对 URL。</param>
/// <param name="Sha256Url">SHA256SUMS.txt 绝对 URL；缺失时下载后拒绝安装（fail loud，宁可误报不装坏包）。</param>
public sealed record ReleaseMeta(string Version, string AssetName, string AssetUrl, string? Sha256Url)
{
    /// <summary>各 RID+包类型的资产文件名后缀（发布命名契约，见 release.yml/package 脚本；rpm 架构名与 deb 不同）。</summary>
    private static readonly Dictionary<string, string> s_ridKindSuffixes = new(StringComparer.Ordinal)
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
        string suffixKey = rid.StartsWith("linux", StringComparison.Ordinal) ? $"{rid}:{pkgKind}" : rid;
        if (!s_ridKindSuffixes.TryGetValue(suffixKey, out string? suffix))
        {
            return null;
        }

        const string downloadSegment = "/releases/download/";
        // href 为站内绝对路径（/owner/repo/releases/download/...）；只认本仓库段——
        // repository 参数同时充当防御校验（页面被替换/缓存串仓时不拿他仓资产当更新装）
        string repoSegment = "/" + repository.Trim('/');
        string? asset = null;
        string? sha = null;
        foreach (string raw in hrefs)
        {
            if (!raw.Contains(downloadSegment, StringComparison.Ordinal) ||
                !raw.StartsWith(repoSegment + "/", StringComparison.Ordinal))
            {
                continue;
            }

            // 归一化为绝对 URL（与 expanded_assets 页同主机，见 ReleaseMetaClient.ExpandedAssetsUrl）
            string href = raw.StartsWith("/", StringComparison.Ordinal) ? $"https://github.com{raw}" : raw;
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
