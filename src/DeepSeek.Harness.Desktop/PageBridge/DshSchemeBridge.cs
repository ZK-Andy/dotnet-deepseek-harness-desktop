using Ryn.Core;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>壳 scheme 桥接（Ryn custom scheme handler 薄适配）：把 <see cref="RynSchemeRequest"/>
/// 译成转发器调用并包回 <see cref="RynSchemeResponse"/>。无决策、无状态（R1 组合根只装配的桥接面）。</summary>
internal static class DshSchemeBridge
{
    /// <summary>构造 scheme handler（闭包转发器实例，由组合根在 <c>BuildApp</c> 装配）。</summary>
    /// <param name="forward">壳转发器（应用单例）。</param>
    /// <returns>Ryn scheme handler 委托。</returns>
    internal static Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> Handler(DshShellForward forward)
    {
        ArgumentNullException.ThrowIfNull(forward);
        return Handler(forward, HostLog.Write);
    }

    /// <summary>同 <see cref="Handler(DshShellForward)"/>，日志回调可注入（测试缝；生产走 <c>HostLog.Write</c>）。
    /// 入口/回包各一 loud 行：mac 转发验证 dispatch 证明"handler 没被调还是回包有问题"的一次定音判据
    /// （验证批终像素是引导页、handler 层零日志，见 ADR shell-mint-and-forward）。值永不落盘：
    /// 壳 URL 不带 token、只记头数/字节数不记头值。</summary>
    /// <param name="forward">壳转发器（应用单例）。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>Ryn scheme handler 委托。</returns>
    internal static Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> Handler(
        DshShellForward forward, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(log);
        return async request =>
        {
            log($"[shell] handler 命中：{request.Method} {request.Url.PathAndQuery}（头{request.Headers?.Count ?? 0}个，体{request.Body.Length}字节）");
            DshShellForward.ForwardResult result = await forward.ForwardAsync(
                request.Method,
                request.Url,
                request.Body.IsEmpty ? null : request.Body.ToArray(),
                request.Headers,
                log).ConfigureAwait(false);
            log($"[shell] handler 回包：{result.Status} {result.ContentType} {result.Body.Length}字节（{request.Method} {request.Url.PathAndQuery}）");
            return new RynSchemeResponse(result.Status, result.ContentType, result.Body);
        };
    }
}
