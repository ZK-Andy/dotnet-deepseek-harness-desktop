namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G0 运行时起步壳：窗口已亮后在后台跑一次启动体（profile 就绪 → 随包插件安装 →
/// CLI shim / spawn / 铸币，由装配面组装注入），只负责单次触发、取消与关窗收口。失败只留痕，
/// 不弹窗、不重启（监督与恢复面留 G2/G3）。首启引导（node/dsh 缺失时的自动安装）不在本片，
/// 随 G1 接回，见 ADR avalonia-g0-entry-switch。</summary>
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
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await _run(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗取消：静默收口，spawn 的进程回收归 ShellResources/宿主生命周期门。
        }
        catch (Exception ex)
        {
            // 失败面只留痕：G0 不装恢复 UI（G2/G3），但绝不静默吞掉启动失败。
            HostLog.Write($"[host] 运行时起步失败：{ex}");
        }
    }
}
