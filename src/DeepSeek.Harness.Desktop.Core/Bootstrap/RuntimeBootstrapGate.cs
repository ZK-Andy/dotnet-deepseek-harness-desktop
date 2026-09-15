namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 引导重试闸门：引导失败后进度页的重试命令（desktop.bootstrap.retry）与引导循环之间的同步点。
/// 信号量语义——Signal 置位；循环轮询 IsSignaled（或消费后 Reset）。独立小类可记序单测（对齐 CloseGate 风格）。
/// </summary>
public sealed class RuntimeBootstrapGate
{
    private TaskCompletionSource _signal = NewTcs();
    private readonly object _lock = new();

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>是否有未消费的重试信号。</summary>
    public bool IsSignaled
    {
        get
        {
            lock (_lock)
            {
                return _signal.Task.IsCompleted;
            }
        }
    }

    /// <summary>置位重试信号（重试按钮触发）。</summary>
    public void Signal()
    {
        lock (_lock)
        {
            _signal.TrySetResult();
        }
    }

    /// <summary>清空未消费的信号（重试循环进入下一轮前调用）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _signal = NewTcs();
        }
    }
}
