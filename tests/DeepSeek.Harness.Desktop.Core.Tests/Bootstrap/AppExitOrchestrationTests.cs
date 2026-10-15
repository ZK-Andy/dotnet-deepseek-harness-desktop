namespace DeepSeek.Harness.Desktop.Core.Tests.Bootstrap;

/// <summary>退出编排用例（ADR app-exit-orchestration-core-usecase）：主循环承载 + 收口顺序经 fake 单测——
/// 主循环正常/抛异常/监督任务故障三种输入下，取消监督器 → 取消引导 → 回收 → 释放监听器的**记序**不变
/// （引导取消除计数外一并进序，防互换两位仍绿）；join 预算用尽即放弃等待（退出不被监督任务拖住）。</summary>
public class AppExitOrchestrationTests
{
    private sealed class FakeBootstrap : IFirstBootBootstrap
    {
        private readonly List<string>? _events;

        public FakeBootstrap(List<string>? events = null) => _events = events;

        public int CancelCalls { get; private set; }
        public bool IsNeeded => false;
        public RuntimeBootstrapGate Gate => new();
        public PreinstallChoiceGate PreinstallGate => new();
        public void Resolve() { }
        public void RegisterCliShim() { }
        public void Start(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady) { }
        public void Cancel()
        {
            CancelCalls++;
            _events?.Add("cancel-bootstrap");
        }

        public Task<bool> WaitSettledAsync(TimeSpan? timeout, CancellationToken ct) => Task.FromResult(true);
    }

    private static (AppExitOrchestration Orchestration, FakeBootstrap Bootstrap, List<string> Events,
                    List<string> Logs, Action Reap, Action Dispose) Setup(TimeSpan? joinBudget = null)
    {
        var cts = new CancellationTokenSource();
        var events = new List<string>();
        var logs = new List<string>();
        cts.Token.Register(() => events.Add("cancel-supervisor"));
        var bootstrap = new FakeBootstrap(events);
        var orchestration = new AppExitOrchestration(
            cts,
            bootstrap,
            joinBudget ?? TimeSpan.FromSeconds(5),
            log: logs.Add);
        return (orchestration, bootstrap, events, logs,
                () => events.Add("reap"),
                () => events.Add("dispose-listener"));
    }

    /// <summary>正常路径：主循环返回后按「取消监督器 → 取消引导 → 回收 → 释放监听器」收口，退出码 0。</summary>
    [Fact]
    public void Run_MainLoopReturns_CancelsReapsAndDisposesInOrder()
    {
        (AppExitOrchestration exit, FakeBootstrap bootstrap, List<string> events, List<string> logs,
            Action reap, Action dispose) = Setup();

        int code = exit.Run(
            () => events.Add("main-loop"),
            Task.CompletedTask,
            reap,
            dispose);

        Assert.Equal(0, code);
        Assert.Equal(["main-loop", "cancel-supervisor", "cancel-bootstrap", "reap", "dispose-listener"], events);
        Assert.Equal(1, bootstrap.CancelCalls);
        Assert.Contains(logs, line => line.Contains("Ryn Run 开始", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("Ryn Run 结束", StringComparison.Ordinal));
    }

    /// <summary>主循环抛异常：异常被吞并留痕，收口照走（窗口已不可用，退出路径不因异常改变）。</summary>
    [Fact]
    public void Run_MainLoopThrows_SwallowsAndStillReaps()
    {
        (AppExitOrchestration exit, FakeBootstrap bootstrap, List<string> events, List<string> logs,
            Action reap, Action dispose) = Setup();

        int code = exit.Run(
            () => throw new InvalidOperationException("run 崩"),
            Task.CompletedTask,
            reap,
            dispose);

        Assert.Equal(0, code);
        Assert.Equal(["cancel-supervisor", "cancel-bootstrap", "reap", "dispose-listener"], events);
        Assert.Equal(1, bootstrap.CancelCalls);
        Assert.Contains(logs, line => line.Contains("Ryn Run 异常", StringComparison.Ordinal)
            && line.Contains("run 崩", StringComparison.Ordinal));
    }

    /// <summary>监督任务已故障：join 抛出的 AggregateException 被吞（该任务随宿主回收而结束），回收照走。</summary>
    [Fact]
    public void Run_FaultedSupervisorTask_SwallowsAggregateAndStillReaps()
    {
        (AppExitOrchestration exit, _, List<string> events, _, Action reap, Action dispose) = Setup();

        int code = exit.Run(
            () => events.Add("main-loop"),
            Task.FromException(new InvalidOperationException("监督崩")),
            reap,
            dispose);

        Assert.Equal(0, code);
        Assert.Equal(["main-loop", "cancel-supervisor", "cancel-bootstrap", "reap", "dispose-listener"], events);
    }

    /// <summary>监督任务超出预算仍未结束：放弃等待直接收口（退出不被监督任务拖住）；监听器未启用传 null 不炸。</summary>
    [Fact]
    public void Run_SupervisorTaskOutlivesBudget_ReturnsAndReaps()
    {
        (AppExitOrchestration exit, _, List<string> events, _, Action reap, _) = Setup(
            joinBudget: TimeSpan.FromMilliseconds(50));
        Task never = new TaskCompletionSource().Task;

        int code = exit.Run(() => events.Add("main-loop"), never, reap, disposeListener: null);

        Assert.Equal(0, code);
        Assert.Equal(["main-loop", "cancel-supervisor", "cancel-bootstrap", "reap"], events);
    }
}
