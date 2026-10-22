namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 每页脚本注册端口（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：旧宿主由
/// <c>IRynWebView.InjectScriptAsync</c> 承担；新宿主实现随 G1 顶栏（首个消费者）接入，
/// 本片只有 Ryn 适配器。注册语义是「此后每次加载执行」，不补投当前文档——当前文档的
/// 一次性注入走 <see cref="IWindowScriptTarget"/>（旧宿主同款分工）。
/// 未就绪（窗口尚未创建/桥未挂载）抛 <see cref="InvalidOperationException"/>，由
/// <see cref="PagePump"/> 的重试单点处置。
/// </summary>
internal interface IPageScriptRegistry
{
    /// <summary>注册「每次页面加载执行」的脚本。</summary>
    /// <param name="script">脚本原文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    Task RegisterOnLoadAsync(string script, CancellationToken ct);
}

/// <summary>
/// 宿主 → 页面事件推送端口（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：旧宿主由
/// <c>IRynWebView.EmitEvent</c> 承担，新宿主经 <c>window.__shell._emit</c>。页面已销毁/取消时丢弃、
/// 其余失败留痕（事件是通知面，不重试——自更新等消费者依赖后续状态变化补送）。
/// </summary>
internal interface IPageEventPublisher
{
    /// <summary>推一条事件。</summary>
    /// <param name="name">事件名。</param>
    /// <param name="payloadJson">载荷 JSON 原文（合法 JSON 表达式）。</param>
    void EmitEvent(string name, string payloadJson);
}
