namespace DeepSeek.Harness.Desktop.Core.Recovery;

/// <summary>
/// 失败面原生对话框端口（R3：外部边界经接口；ADR native-failure-surface）：dsh 退出/重启等待期的失败原因、
/// stderr 尾部与诊断包路径由原生模态承载，宿主不再以脚本覆写活动页 DOM。原生通道不可用的环境由
/// <see cref="IsAvailable"/> 预先表达，调用方据此回落页内恢复面。
/// </summary>
public interface IFailureDialog
{
    /// <summary>本机是否具备承载原生失败模态的能力。false 即调用方必须回落（不得静默丢弃失败面）。</summary>
    bool IsAvailable { get; }

    /// <summary>展示失败模态并等待用户处置（实现方不阻塞调用线程：模态是同步原语）。
    /// <b>契约要害</b>：false 同时表示「用户否定」与「通道故障」（模态建不起来/宿主窗口已关时一律回 false），
    /// 故调用方<b>不得</b>把 false 当破坏性动作的许可——
    /// 破坏性动作只许挂在显式 true 上（壳侧据此把退出只挂「是」分支）。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">对话框正文（失败原因 + 尾部 + 诊断包路径 + 动作说明）。</param>
    /// <param name="ct">取消令牌：只影响等待，不打断已弹出的模态。</param>
    /// <returns>true = 用户选了「是」；false = 用户选「否」<b>或</b>通道故障（同形，故只许 true 触发破坏性动作）。</returns>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);
}
