using System.Diagnostics.CodeAnalysis;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>外部链接判定与打开策略（纯逻辑，可单测）。</summary>
/// <remarks>桌面 WebView 内把站外 <c>http(s)</c> 链接交给系统默认浏览器打开（见 implemented ADR
/// open-external-links-in-system-browser；Ryn 0.32.0 起由导航层 <see cref="RynNavigationCallbacks"/>
/// 在 <c>WebViewNavigating</c> 里消费本判定）。本类只做判定/规约，不依赖 Ryn；打开动作由调用方注入。</remarks>
public static class ExternalLinkPolicy
{
    /// <summary>http/https scheme 集合（OrdinalIgnoreCase）。</summary>
    private static readonly HashSet<string> s_httpSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https" };

    /// <summary>
    /// href 是否应交给系统默认浏览器外部打开。
    /// </summary>
    /// <param name="href">锚点链接的 href（原始字符串）。</param>
    /// <param name="currentOrigin">当前页面 origin（如 <c>http://127.0.0.1:41449</c>）；为 null/空时按"非同 scheme 即外部"处理。</param>
    /// <param name="url">解析结果（绝对 http/https URL），可外部打开时输出。</param>
    /// <returns>true 表示应外部打开；false 表示应放行给页面（SPA 路由/非 http(s)/内部链接）。</returns>
    public static bool IsExternalHttpLink(string? href, string? currentOrigin, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        // 绝对 URL（相对 href 是站内资源，交给 SPA/页面）
        if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? absolute) || !s_httpSchemes.Contains(absolute.Scheme))
        {
            return false;
        }

        url = absolute;

        // 没有可参照的同源信息 → 一律外部打开（保守：WebView 里不该出现无来源的绝对 http 链接走站内）
        if (string.IsNullOrWhiteSpace(currentOrigin))
        {
            return true;
        }

        // 同 scheme + 同 host + 同有效端口视为站内，放行；否则外部
        return !SameOrigin(absolute, currentOrigin);
    }

    /// <summary>判断 <paramref name="url"/> 是否与 <paramref name="origin"/>（如 <c>http://127.0.0.1:41449</c>）同源。</summary>
    private static bool SameOrigin(Uri url, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? baseUri) || baseUri.Scheme != url.Scheme)
        {
            return false;
        }

        if (!string.Equals(baseUri.Host, url.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 端口：默认端口与显式写出的默认端口等价
        return GetEffectivePort(baseUri) == GetEffectivePort(url);
    }

    private static int GetEffectivePort(Uri uri)
    {
        if (uri.IsDefaultPort)
        {
            return uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        }

        return uri.Port;
    }
}
