namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 网页终页探针脚本家（ADR page-verdict-gate）：只读采样当前页 <c>location.origin</c> + 可见文本
/// （400 字截断），按 <c>Core.PageProbeSample</c> 形态拼接后交 <c>Core.WebAuthRecovery.Classify</c> 裁决。
/// 分隔符直接由 <see cref="Core.PageProbeSample.Separator"/> 编译期拼入脚本——两侧不可能漂移。
/// 住 PageBridge（探针先例 <c>PageHealthMonitor.ProbeScript</c>），不住组合根（R1 组合根只装配；R1 S6）。
/// </summary>
internal static class WebAuthProbe
{
    /// <summary>origin + 可见文本采样探针（只读；body 缺失取空串，由裁决判为未知）。</summary>
    public const string Script =
        "(function(){var b=document.body;var t=b&&b.innerText?b.innerText.trim().slice(0,400):'';"
        + "return location.origin+'" + Core.PageProbeSample.Separator + "'+t;})()";
}
