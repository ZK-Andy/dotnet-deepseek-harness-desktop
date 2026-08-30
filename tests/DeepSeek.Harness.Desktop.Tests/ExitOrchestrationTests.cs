using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>有序退出编排序契约（记序回归，ADR child-process-reaping-port-drift /
/// self-update-exit-reaps-dsh-child）：回收三件套先于关窗、先取消监督器再回收 dsh。</summary>
public class ExitOrchestrationTests
{
    private sealed class Recorder
    {
        public List<string> Steps { get; } = new();
        public Action Step(string name) => () => Steps.Add(name);
    }

    [Fact]
    public void ReapRuntime_CancelsSupervisor_BeforeStoppingHost_BeforeReleasingMarker()
    {
        // 先 cancel（掐断监督器恢复分支再 spawn）再 Stop；marker 释放最后（失败不挡前两步）
        var r = new Recorder();
        ExitOrchestration.ReapRuntime(r.Step("cancel"), r.Step("stop"), r.Step("release"));

        Assert.Equal(new[] { "cancel", "stop", "release" }, r.Steps);
    }

    [Fact]
    public void OrderlyQuit_ReapsBeforeDisposeAndClose_ThenWatchdog()
    {
        // 顺序契约：运行时回收先于关窗——hide-to-tray 拦截下未批准的 Close 会吞成隐藏；
        // 监听器释放在回收后、关窗前；看门狗最后启动
        var r = new Recorder();
        ExitOrchestration.OrderlyQuit(
            r.Step("cancel"),
            r.Step("stop"),
            r.Step("release"),
            r.Step("disposeListener"),
            r.Step("closeWindow"),
            r.Step("watchdog"));

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "closeWindow", "watchdog" }, r.Steps);
    }

    [Fact]
    public void OrderlyQuit_NullListener_DoesNotThrow()
    {
        // 单实例监听器未启用（Windows/降级）时 dispose 为 null，编排照常完成
        var r = new Recorder();
        ExitOrchestration.OrderlyQuit(
            r.Step("cancel"), r.Step("stop"), r.Step("release"),
            disposeListener: null,
            r.Step("closeWindow"),
            r.Step("watchdog"));

        Assert.Equal(new[] { "cancel", "stop", "release", "closeWindow", "watchdog" }, r.Steps);
    }
}
