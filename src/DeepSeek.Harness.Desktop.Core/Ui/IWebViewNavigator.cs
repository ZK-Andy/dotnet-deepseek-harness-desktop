namespace DeepSeek.Harness.Desktop.Core.Ui;

/// <summary>
/// 网页视图导航端口（R3：Ryn 是外部边界，端口在 Core、适配器在 Presentation；ADR 组合根机制收官 step-1）：
/// 导航调用、到达信号订阅与脚本求值三面，供进入主界面用例（<see cref="MainUiEntry"/>）与恢复收口导航消费。
/// 端口只做边界搬运——调用时限、提交等待与重试决策留在用例（<c>MainUiEntry</c>），故 fake 端口即可单测整条链。
/// 「窗口未建好」经 <see cref="IsWindowReady"/> 表达；其余异常照抛（fail loud，Ryn「无窗口/未运行」以外的问题
/// 不得被吞成未就绪）。
/// </summary>
public interface IWebViewNavigator
{
    /// <summary>主窗口当前是否可用（等价 <c>CurrentWindowAccessor.Current</c> 的可用性探测；尚未建好 false）。</summary>
    bool IsWindowReady { get; }

    /// <summary>绑定「导航已到达」回调（单槽可变；实现侧转换引擎的导航到达信号）。
    /// 用例在导航前订阅、导航结束后复位为空回调，避免错过提交信号。</summary>
    /// <param name="onArrived">到达回调（引擎回调线程触发，实现侧自行封送）。</param>
    void SetArrivalCallback(Action onArrived);

    /// <summary>发起导航（原生同步段由实现侧隔离进线程池；调用方的有界等待与提交等待在用例侧施加）。</summary>
    /// <param name="target">导航靶点。</param>
    /// <param name="ct">调度取消令牌（已取消即不发起）。</param>
    /// <returns>原生调用返回即完成的任务。</returns>
    Task NavigateAsync(Uri target, CancellationToken ct);

    /// <summary>对当前页求值脚本并返回解码后的文本（桥回报的 JSON 字符串外壳由实现侧还原）。
    /// 本端口不暴露取消位（求值是一段短脚本，端口形状不因取消而变）；取消由调用方对返回任务施加
    /// 有界等待（<c>MainUiEntry</c> 的 <c>WaitAsync(cts.Token)</c>）。</summary>
    /// <param name="script">注入脚本（探针等；脚本是 Presentation 资产，经构造数据交给用例）。</param>
    /// <returns>解码后的返回文本；探针无值 null。</returns>
    Task<string?> EvaluateScriptAsync(string script);
}
