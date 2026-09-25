namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// 收养导航免跳过裁决（纯函数可单测，ADR adopt-skip-navigate-on-self-reload）：市场一键重启的页内
/// <c>doRestart</c> 轮询到新 boot 即 <c>location.reload()</c> 自刷，壳侧收养若再 <c>NavigateAsync</c>
/// 即叠成两跳。恢复周期内观测到同 origin 导航提交（页内自刷）即跳过壳侧导航，只做收养登记。
/// </summary>
public static class AdoptNavigateGate
{
    /// <summary>
    /// 判定收养后是否跳过壳侧导航。
    /// </summary>
    /// <param name="lastNavigatedAtUtc">最近一次导航到达时刻（无到达即 null）。</param>
    /// <param name="relayStartUtc">本次恢复周期起点（恢复屏展示时刻）。</param>
    /// <returns>周期起点后有到达即 true（跳过导航）；否则 false（走既有导航兜底）。</returns>
    public static bool ShouldSkipAdoptNavigate(DateTimeOffset? lastNavigatedAtUtc, DateTimeOffset relayStartUtc) =>
        lastNavigatedAtUtc.HasValue && lastNavigatedAtUtc.Value >= relayStartUtc;
}
