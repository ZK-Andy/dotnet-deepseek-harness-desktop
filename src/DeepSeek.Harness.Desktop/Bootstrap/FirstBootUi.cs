using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>首启引导页反馈适配器（<see cref="IFirstBootUi"/> → <see cref="PagePump"/> 注入单点）：
/// 帧形状与重试语义留在 Presentation，引导服务只见语义方法。</summary>
internal sealed class FirstBootUi : IFirstBootUi
{
    private readonly Func<CurrentWindowAccessor> _accessor;

    /// <summary>创建适配器。</summary>
    /// <param name="accessor">当前窗口访问器提供者；引导仅在 BuildApp 之后运行，调用时点必已就绪。</param>
    public FirstBootUi(Func<CurrentWindowAccessor> accessor) => _accessor = accessor;

    /// <inheritdoc />
    public Task BootstrapStepAsync(BootstrapStep step, string message, bool failed) =>
        PagePump.PushBootstrapStateAsync(_accessor(), step.ToString(), message, failed);

    /// <inheritdoc />
    public Task PreinstallDecisionAsync(IReadOnlyList<string> plugins) =>
        PagePump.RetryPushPreinstallAsync(_accessor(), new PreinstallFrame("decision", Plugins: [.. plugins]));

    /// <inheritdoc />
    public Task PreinstallInstallingAsync(string plugin) =>
        PagePump.RetryPushPreinstallAsync(_accessor(), new PreinstallFrame("installing", Plugin: plugin));

    /// <inheritdoc />
    public Task PreinstallDoneAsync(PreinstallChoice choice, bool? ok, string? message) =>
        PagePump.RetryPushPreinstallAsync(_accessor(), new PreinstallFrame(
            "done",
            Action: choice == PreinstallChoice.Skip ? "skip" : "install",
            Ok: ok,
            Message: message));

    /// <inheritdoc />
    public void PreinstallLog(string line) => PagePump.PushPreinstallLog(_accessor(), line);
}
