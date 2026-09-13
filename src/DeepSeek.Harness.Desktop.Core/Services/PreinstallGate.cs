namespace DeepSeek.Harness.Desktop.Services;

/// <summary>插件引导页的用户决策（ADR reference-alignment 批次二）。</summary>
public enum PreinstallChoice
{
    /// <summary>用户确认安装可选插件。</summary>
    Install,

    /// <summary>用户跳过本次安装（该次不装，可经应用内市场/设置自愈补装）。</summary>
    Skip,
}

/// <summary>
/// 插件引导决策闸门（ADR reference-alignment 批次二）：引导页经 <c>desktop.preinstall.choose</c>
/// 发「确认装/跳过」后置位决策；引导循环 await <see cref="Choice"/> 消费。值携带版
/// <see cref="RuntimeBootstrapGate"/> 语义——Set 置位、Reset 进入下一轮前清空。独立小类可记序单测。
/// </summary>
public sealed class PreinstallChoiceGate
{
    private TaskCompletionSource<PreinstallChoice> _signal = NewTcs();
    private readonly object _lock = new();

    private static TaskCompletionSource<PreinstallChoice> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>待消费的决策任务（未被决策时未完成）。</summary>
    public Task<PreinstallChoice> Choice
    {
        get
        {
            lock (_lock)
            {
                return _signal.Task;
            }
        }
    }

    /// <summary>用户是否已给出决策（未消费前）。</summary>
    public bool IsDecided
    {
        get
        {
            lock (_lock)
            {
                return _signal.Task.IsCompleted;
            }
        }
    }

    /// <summary>置位用户决策（引导页按钮触发；重复置位忽略）。</summary>
    public void Set(PreinstallChoice choice)
    {
        lock (_lock)
        {
            _signal.TrySetResult(choice);
        }
    }

    /// <summary>清空已消费的决策（进入下一轮引导前调用）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _signal = NewTcs();
        }
    }
}
