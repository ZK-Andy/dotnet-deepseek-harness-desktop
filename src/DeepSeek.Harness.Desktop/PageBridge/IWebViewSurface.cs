namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 网页视图可测面（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：桥接只依赖这四个事件与
/// 两个动作，平台适配器（<see cref="NativeWebViewSurface"/>）负责把 Avalonia 事件翻译过来。
/// 抽出本接口的理由与 <see cref="IWindowScriptTarget"/> 同源——桥接逻辑（信封校验、分发、注入时机）
/// 必须有 headless 测试面，不能让测试依赖真实 WebView 引擎。
/// </summary>
internal interface IWebViewSurface
{
    /// <summary>页面消息到达（<c>invokeCSharpAction(body)</c> 的原文）。</summary>
    event EventHandler<PageMessageEventArgs>? MessageReceived;

    /// <summary>顶层导航开始（可取消）。</summary>
    event EventHandler<PageNavigationEventArgs>? NavigationStarted;

    /// <summary>顶层导航完成（含失败）。</summary>
    event EventHandler<PageNavigationEventArgs>? NavigationCompleted;

    /// <summary>页面请求新窗口。</summary>
    event EventHandler<PageNavigationEventArgs>? NewWindowRequested;

    /// <summary>在当前文档求值脚本（未就绪抛 <see cref="InvalidOperationException"/>）。</summary>
    /// <param name="script">待求值脚本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>脚本回报原文；无回报为 null。</returns>
    Task<string?> InvokeScriptAsync(string script, CancellationToken ct);

    /// <summary>重载当前文档（健康探针的有界恢复用）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    Task ReloadAsync(CancellationToken ct);
}

/// <summary>页面消息事件参数。</summary>
internal sealed class PageMessageEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="body">消息原文（UTF-8 文本）。</param>
    public PageMessageEventArgs(string body) => Body = body;

    /// <summary>消息原文。</summary>
    public string Body { get; }
}

/// <summary>导航事件参数（开始/完成/新窗口共用形状）。</summary>
internal sealed class PageNavigationEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="url">目标 URL（未知为 null）。</param>
    /// <param name="isSuccess">导航是否成功（仅完成事件有意义）。</param>
    public PageNavigationEventArgs(Uri? url, bool isSuccess = true)
    {
        Url = url;
        IsSuccess = isSuccess;
    }

    /// <summary>目标 URL。</summary>
    public Uri? Url { get; }

    /// <summary>导航是否成功（仅完成事件有意义）。</summary>
    public bool IsSuccess { get; }

    /// <summary>取消本次导航（仅开始事件消费）。</summary>
    public bool Cancel { get; set; }

    /// <summary>已处理本次新窗口请求（仅新窗口事件消费）。</summary>
    public bool Handled { get; set; }
}
