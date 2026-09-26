namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// dsh 网页终页裁决（ADR webauth-token-reentry + page-verdict-gate）：把探针采样判成
/// <c>healthy</c>／<c>auth</c>／<c>unknown</c> 三态——壳侧自愈据此决定是否重进 token URL，
/// 冒烟门禁据此决定 CI 绿红（绿 = 同源且非鉴权页，不是"到达过"）。
/// 纯函数可单测：编排在组合根，判定只此一处。
/// </summary>
public static class WebAuthRecovery
{
    /// <summary>
    /// 鉴权页标记：dsh 无会话时的 401 纯文本（上游 <c>dsh-client-connection</c> 固定英文串，
    /// 非 UI 词典文案——耦合点刻意小：正常 DSH UI 不含该串，误伤面仅限恰好渲染同串的 dsh 页面）。
    /// 辨析（R3 B1）：自愈重进复 present 的同一 token URL（401 页自带"重开 URL"指引即此语义），
    /// 不是猜新 token——per-process launch token 不可猜，重进永不构造新 token。
    /// </summary>
    public const string AuthRequiredMarker = "authentication required";

    /// <summary>裁决 token（日志行 <c>[nav] 页面裁决=&lt;token&gt;</c> 的取值；冒烟门禁 grep 同串）。
    /// 串由应用侧拥有：改 token 即显示腿缺行转红（fail loud），未置位腿丢 auth 门。</summary>
    public const string VerdictHealthy = "healthy";

    /// <summary>鉴权页裁决 token。</summary>
    public const string VerdictAuth = "auth";

    /// <summary>不可判断裁决 token。</summary>
    public const string VerdictUnknown = "unknown";

    /// <summary>终页三态：健康 / 鉴权页 / 未知（探针失败、非同源、或文本为空）。</summary>
    public enum PageVerdict
    {
        /// <summary>同源、可见文本非空且不含鉴权标记。</summary>
        Healthy,

        /// <summary>同源且可见文本命中鉴权标记。</summary>
        Auth,

        /// <summary>不可判：探针超时/失败、origin 与期望不符、或可见文本为空。</summary>
        Unknown,
    }

    /// <summary>终页裁决明细：三态 + 采样归属（供日志一层留痕，避免二次拆采样）。</summary>
    /// <param name="Verdict">三态裁决。</param>
    /// <param name="HasSample">采样可拆（探针回了数据）时为 true。</param>
    /// <param name="Origin">采样 origin；无采样时为空串。</param>
    /// <param name="VisibleTextLength">可见文本长度；无采样时为 0。</param>
    public readonly record struct PageVerdictDetail(
        PageVerdict Verdict,
        bool HasSample,
        string Origin,
        int VisibleTextLength);

    /// <summary>
    /// 裁决并给出明细（采样只拆一次，日志层用其 origin/长度）。
    /// </summary>
    /// <param name="rawSample">探针原始采样。</param>
    /// <param name="expectedOrigin">期望 origin。</param>
    /// <returns>三态 + 采样明细。</returns>
    public static PageVerdictDetail ClassifyDetail(string? rawSample, string expectedOrigin)
    {
        if (!PageProbeSample.TrySplit(rawSample, out string origin, out string visibleText))
        {
            return new PageVerdictDetail(PageVerdict.Unknown, HasSample: false, string.Empty, 0);
        }

        // 非同源即不可判：占位页（ryn://app）、引导页、错误页都不该被当成"进了 dsh UI"。
        bool sameOrigin = string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase);
        PageVerdict verdict;
        if (!sameOrigin)
        {
            verdict = PageVerdict.Unknown;
        }
        else if (visibleText.Contains(AuthRequiredMarker, StringComparison.Ordinal))
        {
            verdict = PageVerdict.Auth;
        }
        else
        {
            // 同源但文本为空：页面尚未渲染出内容，不足以判健康。
            verdict = string.IsNullOrWhiteSpace(visibleText) ? PageVerdict.Unknown : PageVerdict.Healthy;
        }

        return new PageVerdictDetail(verdict, HasSample: true, origin, visibleText.Length);
    }
}
