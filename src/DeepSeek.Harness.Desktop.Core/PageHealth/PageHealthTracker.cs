namespace DeepSeek.Harness.Desktop.Core.PageHealth;

/// <summary>单次页面探针结论。</summary>
public enum PageHealth
{
    /// <summary>探针异常（窗口未就绪/导航中）——不计入任何计数。</summary>
    Unknown,

    /// <summary>页面有内容。</summary>
    Alive,

    /// <summary>页面空白（body 无子节点）。</summary>
    Dead,
}

/// <summary>
/// 页面健康判定核心（纯逻辑可单测）：连续 <see cref="_deadThreshold"/> 次 Dead 才宣告
/// Dead 迁移——导航瞬间的空窗与渲染间隙不允许触发告警；Unknown 不清零也不累计；
/// 任意 Alive 立即回 Alive 并复位计数。本类只做「看见」与状态迁移判定（迁移留痕日志 +
/// 诊断快照）；是否接自动恢复由 <see cref="PageHealthMonitor"/> 在 Dead 迁移处裁决
/// （ADR reference-alignment 批次五）——误报引发的重启循环比白屏更伤害可用性，故恢复必须
/// 有界（见 <see cref="PageHealthRecovery"/>）。<see cref="ReArm"/> 供恢复路径重置死区。
/// </summary>
public sealed class PageHealthTracker(int deadThreshold = 3)
{
    private readonly int _deadThreshold = deadThreshold > 0 ? deadThreshold : throw new ArgumentOutOfRangeException(nameof(deadThreshold));
    private int _consecutiveDead;

    /// <summary>当前宣告状态（初始 Unknown）。</summary>
    public PageHealth Current { get; private set; } = PageHealth.Unknown;

    /// <summary>累计探针次数（诊断快照用）。</summary>
    public int ProbeCount { get; private set; }

    /// <summary>记录一次探针；发生状态迁移时返回人读描述，否则返回 null。
    /// Unknown 不计入 <see cref="ProbeCount"/>——「probes」语义是有效探针数，窗口未就绪期的
    /// 异常轮询不应让诊断快照里的计数虚高。</summary>
    public string? Record(PageHealth sample)
    {
        if (sample == PageHealth.Unknown)
        {
            return null;
        }

        ProbeCount++;
        switch (sample)
        {
            case PageHealth.Alive:
                _consecutiveDead = 0;
                if (Current == PageHealth.Alive)
                {
                    return null;
                }

                Current = PageHealth.Alive;
                return "页面健康：alive";
            case PageHealth.Dead:
                _consecutiveDead++;
                if (Current != PageHealth.Dead && _consecutiveDead >= _deadThreshold)
                {
                    Current = PageHealth.Dead;
                    return $"页面健康：连续 {_consecutiveDead} 次探针为空（dead）";
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 恢复用：把当前宣告回退到 <see cref="PageHealth.Unknown"/> 并清零连击。用途——监视器触发一次
    /// 有界 reload 后调用，让「重载后仍为空白」的页面重新凑满阈值再触发 Dead 迁移，从而同一假活
    /// 死区内能进行有界多次 reload；否则 <see cref="Current"/> 恒为 <see cref="PageHealth.Dead"/>，
    /// 后续 Dead 采样永不再产生迁移、恢复只能执行一次。<see cref="ProbeCount"/> 不重置（诊断累计）。
    /// </summary>
    public void ReArm()
    {
        _consecutiveDead = 0;
        Current = PageHealth.Unknown;
    }
}
