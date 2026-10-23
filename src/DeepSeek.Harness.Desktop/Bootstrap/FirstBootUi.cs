namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>首启引导页反馈适配器（<see cref="IFirstBootUi"/> → <see cref="PagePump"/> 注入单点）：
/// 帧形状与重试语义留在 Presentation，引导服务只见语义方法。脚本求值端口以提供者注入——
/// 两宿主都在窗口/桥就绪之后才推第一条进度，提供者在调用期取值；桥尚未装配时丢弃该帧
/// （进度是通知面，host.log 仍有步骤留痕，绝不拖垮引导本身）。</summary>
internal sealed class FirstBootUi : IFirstBootUi
{
    private readonly Func<IWindowScriptTarget> _scriptTarget;
    private readonly PagePump _pump;

    /// <summary>创建适配器。</summary>
    /// <param name="scriptTarget">当前窗口脚本求值端口提供者（调用期取值）。</param>
    /// <param name="pump">页面注入单点（装配期创建、与编排期共用同一实例）。</param>
    public FirstBootUi(Func<IWindowScriptTarget> scriptTarget, PagePump pump)
    {
        _scriptTarget = scriptTarget;
        _pump = pump;
    }

    /// <inheritdoc />
    public Task BootstrapStepAsync(BootstrapStep step, string message, bool failed)
    {
        IWindowScriptTarget target;
        try
        {
            target = _scriptTarget();
        }
        catch (InvalidOperationException)
        {
            // 桥/窗口尚未装配：本帧丢弃（重试/失败态由 host.log 与后续帧承担）。
            return Task.CompletedTask;
        }

        return _pump.PushBootstrapStateAsync(target, step.ToString(), message, failed);
    }
}
