using DeepSeek.Harness.Desktop.Core.Ui;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 网页视图导航端口适配器（R3：Ryn 边界留在 Presentation；ADR 组合根机制收官 step-1 的 B1）：
/// 导航调用把原生同步 <c>set_url</c> 段隔离进线程池（Ryn 底层为同步原生调用，直接 <c>WaitAsync</c> 计时器挂不上），
/// 到达信号转 <see cref="RynNavigationCallbacks.SetOnNavigated"/>，脚本求值经 <see cref="IWindowScriptTarget"/>
/// 端口落到唯一的 Ryn 求值适配器，并把桥回报的 JSON 字符串外壳还原
/// （<see cref="RynProbeValue"/>：否则 origin 带引号、同源恒不成立）。时限/重试/降解决策留用例侧。
/// </summary>
internal sealed class WebViewNavigator : IWebViewNavigator
{
    private readonly CurrentWindowAccessor _accessor;
    private readonly IWindowScriptTarget _scriptTarget;
    private readonly RynNavigationCallbacks _callbacks;

    /// <summary>创建适配器。</summary>
    /// <param name="accessor">当前窗口访问器（导航与就绪探测用；原生建窗前 <c>Current</c> 即抛）。</param>
    /// <param name="scriptTarget">当前窗口脚本求值端口（本仓唯一的 Ryn 求值适配器在其后）。</param>
    /// <param name="callbacks">宿主导航回调（到达信号单槽来源）。</param>
    public WebViewNavigator(
        CurrentWindowAccessor accessor,
        IWindowScriptTarget scriptTarget,
        RynNavigationCallbacks callbacks)
    {
        _accessor = accessor;
        _scriptTarget = scriptTarget;
        _callbacks = callbacks;
    }

    /// <inheritdoc />
    public bool IsWindowReady
    {
        get
        {
            try
            {
                _ = _accessor.Current;
                return true;
            }
            catch (InvalidOperationException)
            {
                // 窗口尚未建好（Ryn「无窗口/未运行」同此型）：未就绪；其他异常照抛 fail loud。
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void SetArrivalCallback(Action onArrived) => _callbacks.SetOnNavigated(onArrived);

    /// <inheritdoc />
    public Task NavigateAsync(Uri target, CancellationToken ct) =>
        Task.Run(() => _accessor.Current.NavigateAsync(target).AsTask(), ct);

    /// <inheritdoc />
    public async Task<string?> EvaluateScriptAsync(string script) =>
        RynProbeValue.Decode(
            await _scriptTarget.EvaluateAsync(script, CancellationToken.None).ConfigureAwait(false));
}
