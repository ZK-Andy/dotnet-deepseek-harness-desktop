namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>运行时起步壳：由装配面在窗口构造前触发一次启动体（profile → 插件 → spawn/铸币），只负责
/// 单次触发、取消与关窗收口；失败与「收口后接线」走晚挂载交付（ADR cold-start-wait-collapse）。</summary>
internal sealed class ShellStartup : IDisposable
{
    private readonly Func<CancellationToken, Task> _run;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Action? _afterStart;
    private Action<Exception>? _failureSink;
    private Exception? _pendingFailure;
    private Task? _task;
    private int _started;
    private int _afterStartRan;
    private bool _finished;
    private bool _completed;

    /// <summary>创建起步壳。</summary>
    /// <param name="run">启动体（真实装配在 <c>AvaloniaHost.Run</c>；注入以便并发/取消路径可单测）。</param>
    /// <param name="initialUri">窗口初始 URL（壳 origin；null = 代理未就绪）。</param>
    public ShellStartup(Func<CancellationToken, Task> run, Uri? initialUri)
    {
        _run = run;
        InitialUri = initialUri;
    }

    /// <summary>主窗初始 URL。</summary>
    public Uri? InitialUri { get; }

    /// <summary>起步体收口（成功/失败/取消后完成；未触发为 null）。</summary>
    internal Task? Completion => _task;

    /// <summary>触发一次后台起步（重复调用为 no-op）。</summary>
    public void Begin()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        // 令牌在派发前取好：关窗路径会先 Cancel/Dispose，惰性取 token 会抛 ObjectDisposedException。
        CancellationToken ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct));
    }

    /// <summary>挂上失败处置（窗口就绪后调用）：已失败则立即补交付一次，否则等失败发生。</summary>
    public void AttachFailureSink(Action<Exception> sink)
    {
        Exception? pending;
        lock (_gate)
        {
            pending = _pendingFailure;
            _pendingFailure = null;
            _failureSink = sink;
        }

        if (pending is not null)
        {
            sink(pending);
        }
    }

    /// <summary>挂上收口后的接线（引导与监督）：已成功则立即执行一次，仍在跑则等它返回，失败/取消则永不执行。</summary>
    public void AttachAfterStart(Action afterStart)
    {
        bool runNow;
        lock (_gate)
        {
            if (_completed)
            {
                runNow = true;
            }
            else if (_finished)
            {
                return;
            }
            else
            {
                _afterStart = afterStart;
                runNow = false;
            }
        }

        if (runNow)
        {
            RunAfterStart(afterStart);
        }
    }

    /// <summary>取消起步并有限等待（关窗/退出路径；最多 3 秒）。</summary>
    public void Dispose()
    {
        _cts.Cancel();
        bool finished;
        try
        {
            finished = _task?.Wait(TimeSpan.FromSeconds(3)) ?? true;
        }
        catch (AggregateException)
        {
            // 起步失败已在 RunAsync 内留痕；这里只负责收口，不重复报告。
            finished = true;
        }

        if (!finished)
        {
            HostLog.Write("[host] 关窗时运行时起步未在 3 秒内收口：进程回收交宿主生命周期门兜底");
        }

        _cts.Dispose();
    }

    /// <summary>后台起步体：URL 与铸币日志由启动体自己留痕，此处只负责收口与交付。</summary>
    /// <param name="ct">退出取消令牌。</param>
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await _run(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗取消：静默收口，spawn 的进程回收归 ShellResources/宿主生命周期门。
            Finish(completed: false, failure: null);
            return;
        }
        catch (Exception ex)
        {
            // 先留痕再交付：订阅方（失败面）可能自己再失败，日志是最后的取证面。
            HostLog.Write($"[host] 运行时起步失败：{ex}");
            Finish(completed: false, failure: ex);
            return;
        }

        // 起步体抛异常 = 前置（profile/companion）未完成：失败面已接管，监督器不得在此时
        // 自动重启。正常返回但已取消同样不接监督。返回 null（spawn 未给 URL）不算失败。
        Finish(completed: !ct.IsCancellationRequested, failure: null);
    }

    /// <summary>收口一次：置完成态、取出待跑的接线动作、交付失败（无订阅方则缓存）。</summary>
    private void Finish(bool completed, Exception? failure)
    {
        Action? afterStart = null;
        Action<Exception>? sink = null;
        lock (_gate)
        {
            _finished = true;
            _completed = completed;
            if (completed)
            {
                afterStart = _afterStart;
                _afterStart = null;
            }

            if (failure is not null)
            {
                sink = _failureSink;
                if (sink is null)
                {
                    _pendingFailure = failure;
                }
            }
        }

        if (failure is not null)
        {
            sink?.Invoke(failure);
            return;
        }

        if (afterStart is not null)
        {
            RunAfterStart(afterStart);
        }
    }

    /// <summary>执行收口接线（只执行一次；异常留痕不上抛）。</summary>
    private void RunAfterStart(Action afterStart)
    {
        if (Interlocked.Exchange(ref _afterStartRan, 1) == 1)
        {
            return;
        }

        try
        {
            afterStart();
        }
        catch (Exception ex)
        {
            // 引导/监督接线失败不得盖住起步结论：留痕后收口（失败面已由失败分支承担）。
            HostLog.Write($"[host] 起步后接线失败：{ex}");
        }
    }
}
