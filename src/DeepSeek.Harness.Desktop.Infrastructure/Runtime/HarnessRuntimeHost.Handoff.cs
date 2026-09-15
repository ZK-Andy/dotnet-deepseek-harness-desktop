using System.ComponentModel;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

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

    /// <summary>relay 等待的探测节拍与 helper 宽限窗（测试经内部注入口覆写延迟以压缩时长）。</summary>
    private static readonly TimeSpan s_relayWaitInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_relayHelperGrace = TimeSpan.FromSeconds(2);

    /// <summary>relay 等待的血统残留枚举注入口（生产 null = 真扫 /proc；仅供测试闭环）。</summary>
    internal Func<IReadOnlyList<RuntimeLineage.Subject>>? RelayResidueOverride { get; set; }

    /// <summary>relay 等待的延迟注入口（生产 null = 真延迟；测试用 0 延迟压缩时长）。</summary>
    internal Func<CancellationToken, Task>? RelayDelayOverride { get; set; }

    /// <summary>
    /// 市场接力共存等待（ADR market-restart-adopt-first）：进程内重启入口先观察「市场自重启 helper
    /// 正拉起续任者」的证据，在场则不再 spawn 竞争者——等续任者的 **web 面**就绪（不只是端口可连，
    /// ADR relay-web-readiness）后直接走既有交接处置收养，把 WebView 一次导航到已可服务的页面。helper 不在场或中途消失即快速回落常规探测与 spawn，
    /// 崩溃恢复路径的额外等待不超过一个宽限窗；总预算与恢复时限同源，超时也回落原路径（其 bind
    /// 预探测兜底），绝不因 helper 卡住而推迟恢复。
    /// </summary>
    /// <param name="port">首选端口（监督器在管运行时的端口）。</param>
    /// <param name="supervisedStart">刚退出那个运行时的起始时刻（交接判据参照，此处非 null）。</param>
    /// <param name="timeout">总等待预算（与 RestartAsync 的恢复时限同源）。</param>
    /// <param name="ct">取消令牌；取消恒返回 null（调用方终态短路）。</param>
    /// <returns>命中接力返回收养 URL；无接力证据或窗口耗尽返回 null（调用方按原路径继续）。</returns>
    /// <remarks>internal 供测试直接驱动真实 /proc 探针的接力闭环。</remarks>
    internal async Task<Uri?> TryRideMarketRelayAsync(
        int port,
        DateTimeOffset supervisedStart,
        TimeSpan timeout,
        CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        DateTimeOffset graceEnd = DateTimeOffset.UtcNow + s_relayHelperGrace;
        bool helperSeen = false;
        bool webPendingLogged = false;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            IReadOnlyList<RuntimeLineage.Subject> residue =
                RelayResidueOverride?.Invoke() ?? FindLineageResidue();
            // 证据判定含父链存活核：临终 dsh 生前自拉起、继承 token 的子进程（PTC worker / pnpm 子壳，
            // --profile argv 形状可判 RuntimeServer）同样可证新生，但其父已死——不是本次接力的正主，
            // 不得当无 helper 的「固定证据」把恢复拖到预算上限。
            bool helperNow = false;
            bool evidenceNow = false;
            foreach (RuntimeLineage.Subject s in residue)
            {
                if (!RuntimeLineage.IsRelayEvidence(s, supervisedStart))
                {
                    continue; // 陈旧血统残留（更早诞生/不可证新生）不算证据，在场也不延长等待
                }

                evidenceNow |= IsParentAlive(s);
                helperNow |= s.Kind == RuntimeLineage.LineageKind.MarketRestartHelper;
            }

            helperSeen |= helperNow;
            // 探针按剩余预算收紧（整次探测 ≤ min(剩余预算, 1.5s)）：不会把窗口拖过总预算
            RuntimeLineageProbes.LoopbackWebProbe readiness = await RuntimeLineageProbes
                .ProbeLoopbackWebAsync(port, ct, deadline - DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            if (readiness == RuntimeLineageProbes.LoopbackWebProbe.Ready)
            {
                _log?.Invoke($"[host] 市场接力续任者已接管首选端口 {port}：跳过竞争 spawn，转入收养处置");
                return await RecoverFromFailureAsync(port, StartFailure.PortConflict, supervisedStart, timeout, ct)
                    .ConfigureAwait(false);
            }

            if (!RuntimeLineage.ShouldKeepWaitingRelay(
                    evidenceNow, helperSeen,
                    pastGrace: !helperSeen && DateTimeOffset.UtcNow >= graceEnd,
                    pastDeadline: DateTimeOffset.UtcNow >= deadline))
            {
                // 回落留痕：两条回落原因线都可供 host.log 判读，与接管命中行对偶
                _log?.Invoke($"[host] 市场接力共存窗口回落（{(helperSeen
                    ? "接力中止：helper 已死且无新生续任者"
                    : "无接力证据，宽限窗耗尽")}）→ 走既有探测与 spawn");
                return null;
            }

            // 决定继续等待后留痕：端口可连但 web 面未应答（dsh 先 bind、web-runtime 行后挂载）
            webPendingLogged = LogWebPending(readiness, port, ct, webPendingLogged);

            try
            {
                if (RelayDelayOverride is not null)
                {
                    await RelayDelayOverride(ct).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(s_relayWaitInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // OCE = 调用链取消（终态）：静默回落 null，不视为失败
                return null;
            }
        }
    }

    /// <summary>「端口已监听但 web 面未就绪」一次性留痕（1s 节拍下只记一条；已取消不留痕——那时并未继续等）。</summary>
    /// <param name="readiness">本次就绪判定。</param>
    /// <param name="port">首选端口。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="alreadyLogged">本次等待是否已留痕。</param>
    /// <returns>新的留痕状态。</returns>
    private bool LogWebPending(RuntimeLineageProbes.LoopbackWebProbe readiness, int port, CancellationToken ct, bool alreadyLogged)
    {
        if (readiness != RuntimeLineageProbes.LoopbackWebProbe.ServingNotReady || alreadyLogged || ct.IsCancellationRequested)
        {
            return alreadyLogged;
        }

        _log?.Invoke($"[host] 市场接力续任者端口 {port} 已监听但 web 面未就绪：继续等待，不导航进空白页");
        return true;
    }

    /// <summary>候选的父链是否存活（接力证据的在场判据之一——真 helper/其续任者的父必活，孤儿不算证据）。</summary>
    private static bool IsParentAlive(RuntimeLineage.Subject subject)
    {
        int? parentPid = RuntimeLineageProbes.ReadParentPid(subject.Candidate.Pid);
        return parentPid is int pid && RuntimeLineageProbes.TryIsAlive(pid);
    }

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
        bool portBusy = await RuntimeLineageProbes.IsLoopbackServingAsync(preferred).ConfigureAwait(false);
        RuntimeLineage.PortConflictPlan plan = RuntimeLineage.PlanPortConflict(
            portBusy,
            supervisedStart,
            FindLineageResidue(),
            RuntimeLineageProbes.ReadParentPid);
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
            // 漂移告警（ADR child-process-reaping-port-drift）：观测位不是修复位——origin 变化意味着
            // 上一会话选中态不保留。页面命令通道的后果只在**窗口已按 dsh URL 建好之后**再换端口时成立
            // （Ryn IPC 的 CORS 允许源按建窗时的 opts.Url 钉死，ADR port-drift-ipc-origin-mismatch）：
            // `_port` 非空即「此前已成功起过一次运行时」，非引导路径下那次启动之后窗口才按它的 URL 建好。
            // 首次成功启动即漂移不追加——非引导路径下窗口随后才按漂移后的 origin 创建，允许源与页面
            // origin 一致；无 PATH dsh 的首启引导路径另有形态（opts.Url 为 null、窗口先以占位页创建），
            // 其页面 origin 与允许源的关系不由本判据断言。
            string pageChannelConsequence = _port is not null
                ? "；页面命令通道（自更新/设置开关/诊断）本次会话失效，需重启应用"
                : string.Empty;
            _log?.Invoke($"[host] 首选端口 {preferred} 被占（疑似残留实例或孤儿 dsh），本次漂移至 {fallback.Url.Port}；上一会话选中态将不保留{pageChannelConsequence}");
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
            RuntimeLineageProbes.Enumerate(),
            TrackedPid,
            ResolveDshHome(),
            DesktopProfileName,
            RuntimeLineageProbes.ReadParentPid);

    /// <summary>冷启动 / 启动成功后的一次全量血统收敛（血统扫描之外顺带收割 dsh 下游 scope，见 ADR dsh-sandbox-child-orphan-leak）。</summary>
    /// <param name="reason">日志里的收敛时机说明。</param>
    private void HarvestLineageResidue(string reason)
    {
        HarvestResidue(FindLineageResidue(), reason);
        DshSubprocessScopeReaper.Reap(_log);
    }

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
                RuntimeLineageProbes.KillTree(candidate.Pid);
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
            RuntimeLineageProbes.KillTree(adopted);
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
            if (!RuntimeLineageProbes.TryIsAlive(pid))
            {
                return;
            }

            string? token = RuntimeLineageProbes.ReadToken(pid);
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
