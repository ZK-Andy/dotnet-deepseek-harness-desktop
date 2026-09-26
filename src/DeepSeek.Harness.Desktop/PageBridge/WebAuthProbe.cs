namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 网页会话鉴权探针脚本家（ADR webauth-token-reentry）：只读采样当前页可见文本（400 字截断），
/// 供进入主界面后的鉴权页自愈判定。住 PageBridge（探针先例 <c>PageHealthMonitor.ProbeScript</c>），
/// 不住组合根（R1 组合根只装配；R1 S6）。判定阈值与标记在 <c>Core.WebAuthRecovery</c>。
/// </summary>
internal static class WebAuthProbe
{
    /// <summary>可见文本采样探针（只读；body 缺失返回空串）。</summary>
    public const string Script =
        "(function(){var b=document.body;var t=b?b.innerText:null;return t?t.trim().slice(0,400):'';})()";
}
