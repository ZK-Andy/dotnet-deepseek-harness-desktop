namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 有界恢复预算（纯逻辑可单测，对齐参照项目的 <c>BoundedReloadGate</c>）：页面健康监测在
/// 连续 Dead 达阈值宣告后，仅在预算内允许触发一次有界 reload；成功恢复（Alive）即复位预算
/// 窗口；预算耗尽即转入观测-only（leave 自动恢复面），防误报引发无限重载循环 —— 误报重启
/// 循环比白屏更伤害可用性（ADR reference-alignment 批次五）。
/// </summary>
/// <param name="maxAttempts">单次死区内允许的恢复（reload）次数上限；与
/// <see cref="PageHealthTracker"/> 的死区阈值同属协议型安全常量（参照 <c>BoundedReloadGate</c>
/// 默认 3），非运行时可调参数。</param>
public sealed class PageHealthRecovery(int maxAttempts = 3)
{
    private readonly int _maxAttempts = maxAttempts > 0 ? maxAttempts : throw new ArgumentOutOfRangeException(nameof(maxAttempts));
    private int _attempts;

    /// <summary>已触发的恢复（reload）次数（诊断快照用）。</summary>
    public int Attempts => _attempts;

    /// <summary>预算已耗尽（leave 观测面，不再自动恢复）。由 <see cref="Attempts"/> 派生。</summary>
    public bool Exhausted => _attempts >= _maxAttempts;

    /// <summary>
    /// 一次 Dead 迁移后请求恢复：仍有余量则计入并返回 true（触发 reload）；耗尽返回 false
    /// （转观测，不再触发）。同一死区内的连击不被重复计入——由 <see cref="PageHealthTracker.ReArm"/>
    /// 在触发 reload 时重置死区，下次重新凑满阈值再请求，从而同一假活死区可进行有界多次恢复。
    /// </summary>
    /// <returns>true=本次触发一次恢复（reload）；false=预算已耗尽，转纯观测。</returns>
    public bool TryAllowRecovery()
    {
        if (_attempts >= _maxAttempts)
        {
            return false;
        }

        _attempts++;
        return true;
    }

    /// <summary>
    /// 成功恢复（Alive 迁移）：复位预算窗口——本次死区结束，下次假活（新死区）可重新有界恢复。
    /// </summary>
    public void MarkRecovered() => _attempts = 0;
}
