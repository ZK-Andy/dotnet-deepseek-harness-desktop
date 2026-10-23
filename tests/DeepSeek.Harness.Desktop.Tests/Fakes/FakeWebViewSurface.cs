namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>
/// <see cref="IWebViewSurface"/> 假体（ADR avalonia-g2-shell-bridge）：记录注入脚本、
/// 可排定求值失败，并允许测试按序抬升消息/导航事件——桥接的分发、注入时机与外链拦截都经它做
/// headless 验证，不依赖真实 WebView 引擎。
/// </summary>
internal sealed class FakeWebViewSurface : IWebViewSurface
{
    private readonly object _gate = new();
    private readonly List<string> _scripts = new();
    private readonly Queue<Exception?> _outcomes = new();

    /// <inheritdoc />
    public event EventHandler<PageMessageEventArgs>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler<PageNavigationEventArgs>? NavigationStarted;

    /// <inheritdoc />
    public event EventHandler<PageNavigationEventArgs>? NavigationCompleted;

    /// <inheritdoc />
    public event EventHandler<PageNavigationEventArgs>? NewWindowRequested;

    /// <summary>按调用序记录的脚本。</summary>
    public IReadOnlyList<string> Scripts
    {
        get
        {
            lock (_gate)
            {
                return _scripts.ToArray();
            }
        }
    }

    /// <summary>求值回报原文（默认空串）。</summary>
    public string? ScriptResult { get; set; } = string.Empty;

    /// <summary>按序排定求值要抛的异常；队空即返回空串。</summary>
    /// <param name="outcomes">结局序列。</param>
    public void Enqueue(params Exception[] outcomes)
    {
        lock (_gate)
        {
            foreach (Exception outcome in outcomes)
            {
                _outcomes.Enqueue(outcome);
            }
        }
    }

    /// <inheritdoc />
    public Task<string?> InvokeScriptAsync(string script, CancellationToken ct)
    {
        Exception? outcome;
        lock (_gate)
        {
            _scripts.Add(script);
            outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;
        }

        if (outcome is not null)
        {
            throw outcome;
        }

        return Task.FromResult(ScriptResult);
    }

    /// <summary>可用性（默认 true；置 false 可模拟已销毁的网页视图）。</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>按调用序记录的导航靶点。</summary>
    public List<Uri> Navigations { get; } = [];

    /// <inheritdoc />
    public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task NavigateAsync(Uri target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Navigations.Add(target);
        return Task.CompletedTask;
    }

    /// <summary>抬升一条页面消息。</summary>
    /// <param name="body">消息原文。</param>
    public void RaiseMessage(string body) => MessageReceived?.Invoke(this, new PageMessageEventArgs(body));

    /// <summary>抬升一次导航开始并返回事件参数（供断言 Cancel）。</summary>
    /// <param name="url">目标 URL。</param>
    /// <returns>事件参数。</returns>
    public PageNavigationEventArgs RaiseNavigationStarted(Uri url)
    {
        var args = new PageNavigationEventArgs(url);
        NavigationStarted?.Invoke(this, args);
        return args;
    }

    /// <summary>抬升一次导航完成。</summary>
    /// <param name="url">目标 URL。</param>
    /// <param name="isSuccess">是否成功。</param>
    public void RaiseNavigationCompleted(Uri? url, bool isSuccess = true) =>
        NavigationCompleted?.Invoke(this, new PageNavigationEventArgs(url, isSuccess));

    /// <summary>抬升一次新窗口请求并返回事件参数（供断言 Handled）。</summary>
    /// <param name="url">目标 URL。</param>
    /// <returns>事件参数。</returns>
    public PageNavigationEventArgs RaiseNewWindowRequested(Uri url)
    {
        var args = new PageNavigationEventArgs(url);
        NewWindowRequested?.Invoke(this, args);
        return args;
    }

    /// <summary>等待 index 之后出现满足判据的脚本（轮询至多 2 秒；超时返回 null）。</summary>
    /// <param name="index">起始下标（排除此前已注入的脚本）。</param>
    /// <param name="predicate">判据。</param>
    /// <returns>命中的脚本或 null。</returns>
    public async Task<string?> WaitForScriptAfterAsync(int index, Func<string, bool> predicate)
    {
        for (int i = 0; i < 200; i++)
        {
            IReadOnlyList<string> scripts = Scripts;
            for (int j = index; j < scripts.Count; j++)
            {
                if (predicate(scripts[j]))
                {
                    return scripts[j];
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>等待出现满足判据的脚本（从零开始；轮询至多 2 秒，超时返回 null）。</summary>
    /// <param name="predicate">判据。</param>
    /// <returns>命中的脚本或 null。</returns>
    public Task<string?> WaitForScriptAsync(Func<string, bool> predicate) => WaitForScriptAfterAsync(0, predicate);
}
