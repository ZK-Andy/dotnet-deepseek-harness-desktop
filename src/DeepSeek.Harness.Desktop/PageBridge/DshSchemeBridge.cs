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
        return async request =>
        {
            DshShellForward.ForwardResult result = await forward.ForwardAsync(
                request.Method,
                request.Url,
                request.Body.IsEmpty ? null : request.Body.ToArray(),
                request.Headers,
                HostLog.Write).ConfigureAwait(false);
            return new RynSchemeResponse(result.Status, result.ContentType, result.Body);
        };
    }
}
