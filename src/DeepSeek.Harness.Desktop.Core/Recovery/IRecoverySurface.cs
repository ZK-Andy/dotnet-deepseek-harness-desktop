namespace DeepSeek.Harness.Desktop.Core.Recovery;

/// <summary>
/// 失败面承载端口（R3：外部交互经接口；ADR native-failure-surface / 组合根机制收官 step-1 的 B2）：
/// 原生对话框优先（不碰活动页 DOM），无工具环境回落页内恢复面（覆写活动页 DOM）。展示时机与闩
/// （一恢复周期一次、回落面补收口导航）是用例侧策略，端口只承载两条通道与材料拼装
/// （stderr 尾部、诊断包路径、语言、退出动作）。
/// </summary>
public interface IRecoverySurface
{
    /// <summary>本机是否具备原生承载通道（<c>IFailureDialog.IsAvailable</c> 的投影）；false 即调用方必须走页内回落。</summary>
    bool HasNativeChannel { get; }

    /// <summary>原生失败面（池线程承载，模态是阻塞原语）：用户选「退出应用」时由实现侧完成退出动作。
    /// 异常即通道故障——调用方据此回落页内恢复面（失败面绝不静默消失）。</summary>
    /// <param name="reason">人读失败原因（域决策 <c>UiCopy.RecoveryReason</c> 的产出）。</param>
    /// <param name="ct">取消令牌：只影响等待，不打断已弹出的模态。</param>
    /// <returns>模态处置完成即完成的任务。</returns>
    Task ShowNativeAsync(string reason, CancellationToken ct);

    /// <summary>页内恢复面（回落承载面）：覆写活动页 DOM 展示失败原因与动作按钮；调用方须记住本分支已走，
    /// 恢复后补一次收口导航（ADR recovery-navigation-gate-retirement Decision 3）。</summary>
    /// <param name="reason">人读失败原因。</param>
    /// <returns>注入完成即完成的任务。</returns>
    ValueTask ShowPageAsync(string reason);
}
