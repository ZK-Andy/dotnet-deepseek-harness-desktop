namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>首启引导页反馈适配器（<see cref="IFirstBootUi"/> → <see cref="PagePump"/> 注入单点）：
/// 帧形状与重试语义留在 Presentation，引导服务只见语义方法。</summary>
internal sealed class FirstBootUi : IFirstBootUi
{
    private readonly IWindowScriptTarget _scriptTarget;
    private readonly PagePump _pump;

    /// <summary>创建适配器。</summary>
    /// <param name="scriptTarget">当前窗口脚本求值端口（访问器惰性供给；引导仅在 BuildApp 之后运行，调用时点必已就绪）。</param>
    /// <param name="pump">页面注入单点（装配期创建、与编排期共用同一实例）。</param>
    public FirstBootUi(IWindowScriptTarget scriptTarget, PagePump pump)
    {
        _scriptTarget = scriptTarget;
        _pump = pump;
    }

    /// <inheritdoc />
    public Task BootstrapStepAsync(BootstrapStep step, string message, bool failed) =>
        _pump.PushBootstrapStateAsync(_scriptTarget, step.ToString(), message, failed);
}
