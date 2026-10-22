using System.Text.Json.Serialization.Metadata;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>IRynWebView 假体（自 <c>PagePumpTests</c> 私有嵌套提取为共享假件）：本仓多条注入/注册路径只消费
/// <c>InjectScriptAsync</c>，其余成员为接口完备性占位；注册路可按序排定失败结局。</summary>
internal sealed class FakeWebView : IRynWebView, DeepSeek.Harness.Desktop.PageBridge.IPageScriptRegistry
{
    private readonly Queue<Exception?> _outcomes = new();
    private readonly object _gate = new();
    private int _injectCalls;
    private string? _lastScript;

    /// <summary>未消费（显式存取器，免得「事件从未被使用」告警）。</summary>
    public event EventHandler<FileDropEventArgs>? FileDrop
    {
        add { }
        remove { }
    }

    /// <summary>注册调用次数（含失败尝试）。</summary>
    public int InjectCalls
    {
        get
        {
            lock (_gate)
            {
                return _injectCalls;
            }
        }
    }

    /// <summary>最后一次成功送进的脚本（失败尝试不改它）。</summary>
    public string? LastScript
    {
        get
        {
            lock (_gate)
            {
                return _lastScript;
            }
        }
    }

    /// <summary>按序排定每次注册要抛的异常；队空即注册成功。</summary>
    /// <param name="outcomes">结局序列（异常；空表示成功）。</param>
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

    /// <summary>注册脚本（按排定结局抛或成功）。</summary>
    /// <param name="script">待注册脚本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        Exception? outcome;
        lock (_gate)
        {
            outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;
            if (outcome is null)
            {
                // 先落「最后一次脚本」再计数：等待条件看到计数时该字段必然已就位（无假红窗口）。
                _lastScript = script;
            }

            _injectCalls++;
        }

        if (outcome is not null)
        {
            throw outcome;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>每页加载脚本注册（新端口适配：语义等同 <see cref="InjectScriptAsync"/>）。</summary>
    /// <param name="script">待注册脚本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    public Task RegisterOnLoadAsync(string script, CancellationToken cancellationToken) =>
        InjectScriptAsync(script, cancellationToken).AsTask();

    /// <summary>未消费。</summary>
    /// <param name="url">目标 URL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>未消费。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>未消费。</summary>
    /// <param name="script">脚本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空结果。</returns>
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        new(string.Empty);

    /// <summary>未消费。</summary>
    /// <param name="scheme">自定义 scheme。</param>
    /// <param name="handler">处理器。</param>
    public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler)
    {
    }

    /// <summary>未消费。</summary>
    /// <param name="eventName">事件名。</param>
    /// <param name="jsonData">JSON 载荷。</param>
    public void EmitEvent(string eventName, string jsonData)
    {
    }

    /// <summary>未消费。</summary>
    /// <typeparam name="T">载荷类型。</typeparam>
    /// <param name="eventName">事件名。</param>
    /// <param name="payload">载荷。</param>
    /// <param name="typeInfo">源生成类型信息。</param>
    public void EmitEvent<T>(string eventName, T payload, JsonTypeInfo<T> typeInfo)
    {
    }
}
