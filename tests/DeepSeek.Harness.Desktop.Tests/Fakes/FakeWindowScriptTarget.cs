namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>假窗口脚本求值端口（<see cref="IWindowScriptTarget"/> 替身）：记录每次送进的脚本与取消令牌，
/// 并按序排定每轮结局——排定的 <see cref="Exception"/> 即模拟「窗口尚未就绪」的失败路径
/// （真实适配器在 async 链内抛出、以 faulted Task 到达调用方；本假体同步抛出，对「调用即 await」
/// 的消费者等价）；排定的 <see cref="string"/> 是桥回报原文
/// （如 <c>"text:42"</c>，真实桥回报是 JSON 字符串字面量）；队空即成功（回报 <c>{}</c>）。
/// 队列、计数、「最后一次脚本」与「最后一次取消令牌」都在同一把锁内更新——<see cref="System.Collections.Generic.Queue{T}"/>
/// 本身非线程安全，出锁读写会与池线程写入交错（ADR architecture/2026-10-21-window-script-target-port）。
/// </summary>
internal sealed class FakeWindowScriptTarget : IWindowScriptTarget
{
    private readonly Queue<object?> _outcomes = new();
    private readonly List<string> _scripts = [];
    private readonly object _gate = new();
    private string? _lastScript;
    private CancellationToken _lastCancellationToken;

    /// <summary>每次求值的脚本（含失败尝试），按发生序。</summary>
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

    /// <summary>成功送进的最后一次脚本（失败尝试不改它）。</summary>
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

    /// <summary>最近一次调用收到的取消令牌（透传核对用）。</summary>
    public CancellationToken LastCancellationToken
    {
        get
        {
            lock (_gate)
            {
                return _lastCancellationToken;
            }
        }
    }

    /// <summary>按序排定失败结局；队空即成功。</summary>
    /// <param name="outcomes">结局序列（异常 = 本轮失败）。</param>
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

    /// <summary>按序排定成功结局（桥回报原文，如 JSON 字符串字面量 <c>"text:42"</c>）。</summary>
    /// <param name="results">回报原文序列。</param>
    public void EnqueueResult(params string[] results)
    {
        lock (_gate)
        {
            foreach (string result in results)
            {
                _outcomes.Enqueue(result);
            }
        }
    }

    /// <inheritdoc />
    public Task<string> EvaluateAsync(string script, CancellationToken ct)
    {
        object? outcome;
        lock (_gate)
        {
            _lastCancellationToken = ct;
            outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;
            if (outcome is not Exception)
            {
                // 先落「最后一次脚本」再计数：等待条件看到计数时该字段必然已就位（无假红窗口）。
                // 只在成功结局落（失败尝试不改它）——与属性 doc 同口径：排定的成功回报也算成功求值。
                _lastScript = script;
            }

            _scripts.Add(script);
        }

        if (outcome is Exception failure)
        {
            throw failure;
        }

        return Task.FromResult(outcome as string ?? "{}");
    }
}
