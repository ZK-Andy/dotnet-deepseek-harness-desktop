namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 网页终页探针脚本家（ADR page-verdict-gate）：只读采样当前页 <c>location.origin</c> + 可见文本
/// （400 字截断），按 <c>Core.PageProbeSample</c> 形态拼接后交 <c>Core.WebAuthRecovery.Classify</c> 裁决。
/// 分隔符直接由 <see cref="Core.PageProbeSample.Separator"/> 编译期拼入脚本——两侧不可能漂移。
/// 住 PageBridge，不住装配面（R1 装配面只装配；R1 S6）。
/// </summary>
internal static class WebAuthProbe
{
    /// <summary>origin + 可见文本采样探针（只读；body 缺失取空串，由裁决判为未知）。</summary>
    public const string Script =
        "(function(){var b=document.body;var t=b&&b.innerText?b.innerText.trim().slice(0,400):'';"
        + "return location.origin+'" + Core.PageProbeSample.Separator + "'+t;})()";

    /// <summary>holder 过渡页判据探针（回报 <c>holder</c>/<c>ui</c>/<c>unknown</c>）：判据与代理服务页
    /// 同一标记（<see cref="Core.WebAuthRecovery.HolderMarker"/>），不另造判词；文档尚未解析（无 body/无文本）
    /// 或求值异常一律报 <c>unknown</c>，调用点须按「未就绪」处理而不是当已就绪。</summary>
    /// <param name="holderMarker">holder 页标记。</param>
    /// <returns>探针脚本。</returns>
    public static string HolderScript(string holderMarker) =>
        "(function(){try{var b=document.body;var t=(b&&b.innerText)||'';if(!t)return 'unknown';"
        + "return t.indexOf(" + AppJsonContext.JsString(holderMarker) + ")>=0?'holder':'ui';}"
        + "catch(e){return 'unknown';}})();";
}
