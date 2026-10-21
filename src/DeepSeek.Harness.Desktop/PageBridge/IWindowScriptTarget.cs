namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 当前窗口脚本求值端口（R3：Ryn 窗口边界经接口；ADR architecture/2026-10-21-window-script-target-port）：
/// 页面注入（横幅/顶栏/引导进度）、失败面页内恢复面、网页视图探针与页面健康探针的共同动作只有一条——
/// 对当前窗口求值一段 JS。抽成端口后这些用例不再依赖 Ryn 的 <c>CurrentWindowAccessor</c>（无公开构造），
/// 且 Ryn 脚本求值面在本仓只有一处适配器（<see cref="WindowScriptTarget"/>）。
/// 回报解码也只有一处（<see cref="RynProbeValue.Decode"/>）——端口交回桥回报原文，解码由消费侧在解码点做。
/// </summary>
internal interface IWindowScriptTarget
{
    /// <summary>对当前窗口求值脚本。窗口尚未就绪时抛 <see cref="InvalidOperationException"/>——
    /// 各消费方各自处置：<c>PagePump</c> 的注入路按节拍重试，页面健康探针记为未知并静默续跑；
    /// 其余异常照抛。</summary>
    /// <param name="script">待求值的脚本。</param>
    /// <param name="ct">取消令牌（沿链透传至 Ryn 求值面）。</param>
    /// <returns>桥回报的原文（解码见 <see cref="RynProbeValue.Decode"/>）；未就绪以外的失败以异常表达。</returns>
    Task<string> EvaluateAsync(string script, CancellationToken ct);
}
