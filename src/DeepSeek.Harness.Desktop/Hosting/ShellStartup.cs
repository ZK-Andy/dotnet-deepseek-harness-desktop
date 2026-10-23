namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>运行时起步壳：窗口已亮后在后台跑一次启动体（profile 就绪 → 随包插件安装 →
/// CLI shim / spawn / 铸币，由装配面组装注入），只负责单次触发、取消与关窗收口。失败留痕并
/// 发 <see cref="Failed"/>（失败面承载在 <see cref="StartupFailureFace"/>，本类不碰 UI）。
/// 起步收口后的首启引导与监督接线经 <see cref="Begin"/> 的回调交装配侧（本类不持监督器）。</summary>
internal sealed class ShellStartup : IDisposable
{
    private readonly Func<CancellationToken, Task> _run;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private int _started;

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

    /// <summary>启动体失败（后台线程触发；订阅方自行封送 UI 线程）。</summary>
    public event Action<Exception>? Failed;

    /// <summary>触发一次后台起步（重复调用为 no-op）。</summary>
    /// <param name="afterStart">起步体收口后的接线动作（引导启动与监督循环）——只在起步体正常返回
    /// （含 spawn 未给 URL 的降级）且未取消时调用；起步体抛异常时由启动失败面接管，不调。
    /// 回调自身异常只留痕，不再上抛。</param>
    public void Begin(Action? afterStart = null)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        // 令牌在派发前取好：关窗路径会先 Cancel/Dispose，惰性取 token 会抛 ObjectDisposedException。
        CancellationToken ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct, afterStart));
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

    /// <summary>后台起步体：URL 与铸币日志由启动体自己留痕，此处只兜异常。</summary>
    /// <param name="ct">退出取消令牌。</param>
    /// <param name="afterStart">起步收口后的接线动作（见 <see cref="Begin"/>）。</param>
    private async Task RunAsync(CancellationToken ct, Action? afterStart)
    {
        bool failed = false;
        try
        {
            await _run(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗取消：静默收口，spawn 的进程回收归 ShellResources/宿主生命周期门。
            return;
        }
        catch (Exception ex)
        {
            // 先留痕再发事件：订阅方（失败面）可能自己再失败，日志是最后的取证面。
            failed = true;
            HostLog.Write($"[host] 运行时起步失败：{ex}");
            Failed?.Invoke(ex);
        }

        // 起步体抛异常 = 前置（profile/companion）未完成：失败面已接管，监督器不得在此时
        // 自动重启（否则在未就绪的宿主上再 spawn 一次）。返回 null（spawn 未给 URL）不算失败。
        if (failed || afterStart is null || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            afterStart();
        }
        catch (Exception ex)
        {
            // 引导/监督接线失败不得盖住起步结论：留痕后收口（失败面已由 Failed 分支承担）。
            HostLog.Write($"[host] 起步后接线失败：{ex}");
        }
    }
}
