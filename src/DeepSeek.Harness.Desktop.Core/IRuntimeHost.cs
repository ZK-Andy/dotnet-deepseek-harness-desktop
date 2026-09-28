namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// 运行时宿主端口（R3：接口在 Core、实现在 Infrastructure，ADR runtime-supervisor-core-port）：
/// 监督策略（崩溃恢复/重启退避/退出码处置）所需的 dsh 进程交互面。端口只收敛监督器的消费集，
/// 不追求宿主全貌——新的消费方按 MVP 判据（解锁测试或消除重复决策）再扩。
/// </summary>
public interface IRuntimeHost
{
    /// <summary>子进程 stderr 内存尾巴（死前落盘留证用）。</summary>
    IReadOnlyList<string> StderrTail { get; }

    /// <summary>残留预检：verified 僵尸存在且杀不掉/不敢杀时 true（监督器跳过本轮重启，fail loud）。</summary>
    bool TryDetectUnreapableResidue();

    /// <summary>重启 dsh 并等待其 web URL；时限内未给出返回 null。</summary>
    Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>等待子进程退出（当前无子进程时立即完成）。</summary>
    Task WaitForExitAsync();
}
