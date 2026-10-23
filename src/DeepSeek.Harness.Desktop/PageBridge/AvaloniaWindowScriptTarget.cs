namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// <see cref="IWindowScriptTarget"/> 的 Avalonia 适配器（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：
/// 注入/探针的「对当前文档求值」只有这一条路径，与旧宿主的 <see cref="WindowScriptTarget"/> 同职。
/// 未就绪（文档未创建/已销毁）由适配器抛 <see cref="InvalidOperationException"/>——与重试单点的
/// 「未就绪」判据同源；回报原文不在此解码（解码仍归 <see cref="RynProbeValue"/> 唯一一处）。
/// </summary>
internal sealed class AvaloniaWindowScriptTarget : IWindowScriptTarget
{
    private readonly Func<IWebViewSurface?> _resolve;

    /// <summary>创建适配器（网页视图已就绪的固定形态）。</summary>
    /// <param name="surface">网页视图可测面。</param>
    public AvaloniaWindowScriptTarget(IWebViewSurface surface) : this(() => surface)
    {
    }

    /// <summary>创建惰性适配器（网页视图在装配之后才挂载：引导进度/恢复面在调用期取当面）。</summary>
    /// <param name="resolve">网页视图可测面取法；null = 尚未挂载，求值即抛未就绪。</param>
    public AvaloniaWindowScriptTarget(Func<IWebViewSurface?> resolve) => _resolve = resolve;

    /// <inheritdoc />
    public async Task<string> EvaluateAsync(string script, CancellationToken ct)
    {
        IWebViewSurface surface = _resolve() ?? throw new InvalidOperationException("网页视图尚未就绪");
        return await surface.InvokeScriptAsync(script, ct).ConfigureAwait(false) ?? string.Empty;
    }
}
