using System.ComponentModel;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// <see cref="HarnessRuntimeHost"/> 的端口交接面（partial）：首选端口启动失败时的处置——收养市场原地接力
/// 拉起的续任者、收割血统残留、或在端口被非血统进程占用时回退 OS 分配。判据与探针见
/// <see cref="RuntimeLineage"/>；生命周期与 spawn 主体见 <c>HarnessRuntimeHost.cs</c>。
/// </summary>
/// <remarks>
/// 为什么需要这一面：市场（dshmarket）对未被识别为受管宿主的 dsh 保留「一键自重启」——它先 SIGTERM 当前
/// dsh，再由 detached helper 用**同一个端口**拉起续任者（原地接力，origin 不变）。壳同期也会因「子进程退出」
/// 触发恢复并按同一端口 spawn，两个重启者抢同一个端口：先 bind 者胜。壳输了就会漂移到新端口（origin 变化
/// = 上一会话选中态丢失），而市场的续任者从来不是壳的子进程，`_process`、整树击杀与 `.dsh-pid` 记录都不
/// 覆盖它。这里的处置把「输了」变成可用结局：可证诞生于刚退出那个运行时之后的血统服务端就地**收养**
/// （登记 pid 供退出时回收、导航回裸 origin，WebView 里同 authority 的会话 cookie 仍有效）；更早的血统
/// 残留则**收割**后重试首选端口；只有占用者非我方血统时才回退漂移并告警。
/// </remarks>
public sealed partial class HarnessRuntimeHost
{
    /// <summary>收养的市场接力续任者 pid（非本进程子进程；退出时整树收割）。null = 无收养。</summary>
    private int? _adoptedPid;

    /// <summary>收养时该续任者环境里的血统 token：判活时复验身份，防 pid 复用让监督器永远看不到退出。</summary>
    private string? _adoptedToken;

    /// <summary>当前在管运行时的起始时刻：「新生续任者」判据的参照（冷启动或尚未成功启动时为 null）。</summary>
    private DateTimeOffset? _runtimeStartedUtc;

    /// <summary>在管运行时 pid：本进程子进程优先，其次收养的续任者；皆无则 null。</summary>
    private int? TrackedPid => _process is { HasExited: false } process ? process.Id : _adoptedPid;

    /// <summary>首选端口启动失败后的恢复：按血统判据决定收养续任者 / 收割残留重试 / 回退漂移。</summary>
    /// <param name="preferred">首选端口。</param>
    /// <param name="failure">失败原因（日志措辞用）。</param>
    /// <param name="supervisedStart">刚退出那个运行时的起始时刻（收养判据的参照）。</param>
    /// <param name="timeout">单次尝试等待 URL 的时限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用 URL；三条路都拿不到时返回 null（调用方按启动失败处理）。</returns>
    private async Task<Uri?> RecoverFromFailureAsync(
        int preferred,
        StartFailure failure,
        DateTimeOffset? supervisedStart,
        TimeSpan timeout,
        CancellationToken ct)
    {
        bool portBusy = await RuntimeLineage.IsLoopbackServingAsync(preferred).ConfigureAwait(false);
        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(
            portBusy,
            supervisedStart,
            FindLineageResidue(),
            RuntimeLineage.ReadParentPid);
        _log?.Invoke($"[host] 首选端口 {preferred} 启动失败（{DescribeFailure(failure)}）：处置 {plan.Action}"
            + $"（端口{(portBusy ? "仍被占" : "已空")}，血统残留 {plan.Harvest.Count} 项）");

        if (plan.Action == RuntimeLineage.PortConflictAction.Adopt && plan.Successor is not null)
        {
            HarvestResidue(plan.Harvest, "收养续任者前收割其他残留");
            return AdoptSuccessor(preferred, plan.Successor);
        }

        HarvestResidue(plan.Harvest, "启动失败后收割血统残留");

        if (plan.Action != RuntimeLineage.PortConflictAction.Fallback)
        {
            // 端口本该已空出（原本就空 / 残留刚被收割）：重试首选端口，origin 不变
            StartAttempt retry = await StartCoreAsync(preferred, timeout, ct).ConfigureAwait(false);
            if (retry.Url is not null)
            {
                return retry.Url;
            }

            _log?.Invoke($"[host] 首选端口 {preferred} 重试仍失败（{DescribeFailure(retry.Failure)}）");
        }

        StartAttempt fallback = await StartCoreAsync(null, timeout, ct).ConfigureAwait(false);
        if (fallback.Url is not null)
        {
            // 漂移告警（ADR child-process-reaping-port-drift）：观测位不是修复位——
            // origin 变化意味着上一会话选中态不保留，日志给出人可判读的残留信号
            _log?.Invoke($"[host] 首选端口 {preferred} 被占（疑似残留实例或孤儿 dsh），本次漂移至 {fallback.Url.Port}；上一会话选中态将不保留");
        }

        return fallback.Url;
    }

    /// <summary>收养市场接力拉起的续任者：登记 pid、把在管记录改指向它，并以裸 origin 返回 URL。</summary>
    /// <param name="port">续任者原地服务的端口（= 首选端口）。</param>
    /// <param name="successor">判定为新生续任者的血统残留项。</param>
    /// <returns>裸 origin URL（不带一次性 launch token）。</returns>
    /// <remarks>URL 不带 <c>?token=</c> 是有意的：dsh 的会话 cookie 由落盘持久密钥签名、按 authority
    /// （host:port）绑定，同端口重启后 WebView 里那枚 cookie 仍有效；壳拿不到也不该去猜续任者的 per-process token。</remarks>
    private Uri AdoptSuccessor(int port, RuntimeLineage.Subject successor)
    {
        RuntimeLineage.Candidate candidate = successor.Candidate;
        _adoptedPid = candidate.Pid;
        _adoptedToken = candidate.Token;
        _runtimeStartedUtc = candidate.StartTime ?? DateTimeOffset.UtcNow;
        // 在管记录改指续任者：即使壳之后异常死亡，下次冷启动的 token 复验仍指向真正在服务的那个进程
        PersistSpawn(candidate.Pid, candidate.Token);
        _log?.Invoke($"[host] 收养市场接力的续任者：pid {candidate.Pid} 原地服务端口 {port}（壳不再抢端口；退出时整树收割）");
        return new Uri($"http://127.0.0.1:{port}/");
    }

    /// <summary>枚举血统残留：属我方血统且不在在管运行时树内（Linux <c>/proc</c>；其他平台返回空）。</summary>
    /// <returns>残留项（含市场 helper：它只会催生竞争者）。</returns>
    private IReadOnlyList<RuntimeLineage.Subject> FindLineageResidue() =>
        RuntimeLineage.SelectResidue(
            RuntimeLineage.Enumerate(),
            TrackedPid,
            ResolveDshHome(),
            DesktopProfileName,
            RuntimeLineage.ReadParentPid);

    /// <summary>冷启动 / 启动成功后的一次全量血统收敛。</summary>
    /// <param name="reason">日志里的收敛时机说明。</param>
    private void HarvestLineageResidue(string reason) => HarvestResidue(FindLineageResidue(), reason);

    /// <summary>整树收割血统残留（best-effort：单个失败只留痕，绝不阻断启动）。</summary>
    /// <param name="residue">残留项。</param>
    /// <param name="reason">日志里的收割缘起。</param>
    private void HarvestResidue(IReadOnlyList<RuntimeLineage.Subject> residue, string reason)
    {
        foreach (RuntimeLineage.Subject subject in residue)
        {
            RuntimeLineage.Candidate candidate = subject.Candidate;
            try
            {
                RuntimeLineage.KillTree(candidate.Pid);
                _log?.Invoke($"[host] 收割桌面运行时残留（{reason}）：pid {candidate.Pid} kind={subject.Kind}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // 进程恰好已退出 / 无权限：残留自行消失或不可动，留痕即可
                _log?.Invoke($"[host] 收割残留失败（进程已退出或无权限）：pid {candidate.Pid} {ex.Message}");
            }
        }
    }

    /// <summary>收割收养的运行时的进程树（Stop/Dispose 路径；已是子进程者由 <c>_process</c> 分支负责）。</summary>
    private void KillAdoptedRuntime()
    {
        if (_adoptedPid is not int adopted)
        {
            return;
        }

        try
        {
            RuntimeLineage.KillTree(adopted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 收养的运行时恰好已退出 / 无权限：留痕（残留对 host.log 必须可见）
            _log?.Invoke($"[host] 回收收养的续任者失败（进程已退出或无权限）：pid {adopted} {ex.Message}");
        }

        _adoptedPid = null;
        _adoptedToken = null;
    }

    /// <summary>收养运行时的退出等待：非本进程子进程挂不上 <c>Exited</c>，按 1s 轮询判活。</summary>
    /// <param name="pid">收养的 pid。</param>
    /// <param name="expectedToken">收养时该进程的血统 token（复验身份）。</param>
    /// <returns>进程消失即完成；应用退出路径的 Stop 会杀掉它，故不会无限轮询。</returns>
    /// <remarks>判活必须带身份复验：pid 复用（正是本仓用 token 复验防范的同一风险）会让「/proc 存在」恒真，
    /// 监督器将永远看不到该运行时退出、会话崩溃恢复静默失效。token 读不到（无权限/僵尸）按「仍在运行」
    /// 处理——宁可晚恢复，也不把活着的运行时判死而触发抢端口。</remarks>
    private static async Task WaitAdoptedExitAsync(int pid, string expectedToken)
    {
        while (true)
        {
            if (!RuntimeLineage.TryIsAlive(pid))
            {
                return;
            }

            string? token = RuntimeLineage.ReadToken(pid);
            if (token is not null && !string.Equals(token, expectedToken, StringComparison.Ordinal))
            {
                // pid 已复用给别的进程：当初收养的运行时已经退出
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }

    /// <summary>失败原因的日志措辞。</summary>
    /// <param name="failure">失败原因；null（成功）与未知一律落到「未在时限内给出 URL」。</param>
    /// <returns>人可判读的中文短语。</returns>
    private static string DescribeFailure(StartFailure? failure) => failure switch
    {
        StartFailure.PortConflict => "首选端口被占（EADDRINUSE）",
        StartFailure.EarlyExit => "子进程未给出 URL 即退出",
        StartFailure.Cancelled => "已取消",
        _ => "未在时限内给出 URL",
    };
}
