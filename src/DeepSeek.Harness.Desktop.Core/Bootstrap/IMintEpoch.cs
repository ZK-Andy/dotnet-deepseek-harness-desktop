namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 壳铸币态端口（R3：铸币/收养重验是外部边界，端口在 Core、适配器在 Presentation 经 Infrastructure 的
/// <c>DshShellForward</c> 接入；ADR 组合根机制收官 step-1 的 B2）：dsh 进程死亡时失效门控、重启给出新端点时
/// 以现持凭据重验，供监督用例消费。失败 loud 不抛（实现侧留痕），取消照常上抛。
/// </summary>
public interface IMintEpoch
{
    /// <summary>铸币态失效（dsh 进程退出时调用）：路由清空、holder 门控重新武装（cookie 保留待收养重验）。</summary>
    /// <returns>true = 本次确有失效翻转；false = 已处失效态（无动作）。</returns>
    bool Invalidate();

    /// <summary>收养重验（token-free 的 epoch 重铸）：稳定即重指路由并放行门控；链断由实现侧 loud 留痕
    /// （降级路径在壳转发面自持，调用方无分支可做——结果不设返回值）。</summary>
    /// <param name="url">重启后的 dsh 端点（仅供重验与日志；值永不落盘）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重验走完即完成的任务。</returns>
    Task RevalidateAsync(DshWebUrl url, CancellationToken ct);
}
