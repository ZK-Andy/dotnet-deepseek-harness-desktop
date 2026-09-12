using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>托管 dsh 运行时子进程：spawn dsh（`--profile <see cref="HarnessRuntimeHost.DesktopProfileName"/> --port 0`），解析 `dsh web:` URL，管理生命周期。
/// 静态路径/环境解析面见 <c>HarnessRuntimeHost.Paths.cs</c>；端口冲突的交接处置（收养市场接力的续任者 /
/// 收割血统残留 / 回退漂移）见 <c>HarnessRuntimeHost.Handoff.cs</c>（两者皆 partial）。</summary>
/// <remarks>对应 implemented architecture ADR shared-home-desktop-profile：壳只负责运行时生命周期，组合的 Harness
/// 插件树即应用运行时；产品态默认上游规范共享 home `~/.dsh`，专属 `profiles/dotnet-desktop` 承载插件装配
/// （0.1.5-alpha.1 起上游 CLI 圈占字面名 desktop 给官方 Electron 端，故改名，见 ADR desktop-profile-rename）。
/// 全局 dsh 模型（ADR simple-shell-single-global-dsh）：宿主恒以 PATH 上的 <c>dsh</c> 运行，无捆绑形态。</remarks>
public sealed partial class HarnessRuntimeHost : IDisposable
{
    private const int StderrTailCapacity = 40;

    private readonly Action<string>? _log;
    private int? _port;
    private Process? _process;

    /// <summary>Start/Stop/Restart 生命周期串行化门（防并发双 spawn/_process 覆盖互踩）。</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private readonly List<string> _stderrTail = new(StderrTailCapacity);
    private readonly object _stderrLock = new();

    /// <summary>创建运行时宿主（恒以 PATH 上的全局 dsh 形态运行）。</summary>
    /// <param name="log">日志回调（可选）：端口漂移等运行时决策留痕 host.log（缺省 null 安全）。</param>
    public HarnessRuntimeHost(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>本次采用的运行时描述（日志/恢复屏用）。</summary>
    public string RuntimeDescription => "PATH dsh";

    /// <summary>失败时可读的诊断尾巴（stderr 末 N 行）。</summary>
    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_stderrLock)
            {
                return _stderrTail.ToArray();
            }
        }
    }

    /// <summary>启动 dsh web（OS 分配端口），等待 `dsh web:` URL 或超时。</summary>
    /// <param name="timeout">等待 URL 的时限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>dsh web UI 的 URL；未在时限内给出、或启动在生命周期门等待期即被取消则为 null
    /// （门内 spawn 后的取消仍会抛 <see cref="OperationCanceledException"/>，由消费方按退出处理）。</returns>
    /// <remarks>端口需跨 App 冷启动保持稳定：origin 不变 → dsh Web 端"当前会话"localStorage（dsh.sessions.current，按 origin 隔离）
    /// 仍命中 → 恢复上一会话。进程内崩溃重启复用 <paramref name="ct"/> 前记忆的 <c>_port</c>；冷启动从磁盘加载上次端口并回写。</remarks>
    public async Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // 生命周期串行化门：随包插件安装任务与崩溃监督器的重启可并发进入，无门时
        // 双 spawn / _process 覆盖会把前一个 dsh 失管成孤儿（与 orderlyQuit 看门狗封堵的
        // spawn-after-cancel 竞态同族）。
        try
        {
            await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是终态：入口检查先于 spawn，监督器恢复分支撞上退出时绝不留下无人认领的 dsh 孤儿
            return null;
        }

        try
        {
            StopCore();
            return await StartInnerAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>StartAsync 的门内主体（Stop 之后的部分）：残留收敛 + spawn + 交接处置 + 端口记忆。</summary>
    private async Task<Uri?> StartInnerAsync(TimeSpan timeout, CancellationToken ct)
    {
        // 冷启动（_port 未初始化）时收敛上次遗留的运行时残留（ADR self-update-exit-reaps-dsh-child 缺口 B）。
        // 两条判据并用：.dsh-pid 记录复验（跨平台快路径）+ 血统扫描（记录被后续 spawn 覆盖后的主路径）。
        // 仅冷启动做：进程内重启时在管运行时由 StopCore 与交接处置负责。
        bool coldStart = _port is null;
        if (coldStart)
        {
            _log?.Invoke($"[host] 冷启动：清扫孤儿 dsh（{ResolvePidFilePath()}）");
            OrphanDshReaper.Reap(
                ResolvePidFilePath(),
                RuntimeLineage.ReadToken,
                RuntimeLineage.KillTree,
                _log);
            HarvestLineageResidue("冷启动");
        }

        int? preferred = _port ?? TryLoadPersistedPort();
        // 交接判据的参照：刚退出那个运行时的起始时刻（必须在本次尝试覆盖 _runtimeStartedUtc 之前取）
        DateTimeOffset? supervisedStart = _runtimeStartedUtc;
        Uri? url;
        if (ct.IsCancellationRequested)
        {
            // 已取消：跳过探测与处置链直接短路，避免多付一次注定取消的 bind 探测与日志副作用
            url = null;
        }
        else if (preferred is int busyPort && RuntimeLineage.ProbeLoopbackBind(busyPort) == RuntimeLineage.LoopbackBindProbe.Occupied)
        {
            // spawn 前自有 bind 探测（ADR port-wait-compression）：dsh 要先加载整棵插件树才 bind
            // （实测 42–47s），端口已被占时这个尝试注定失败——跳过 spawn 直接进交接处置。
            _log?.Invoke($"[host] 首选端口 {busyPort} 探测已被占：跳过注定失败的启动尝试，直接进入交接处置");
            url = await RecoverFromFailureAsync(busyPort, StartFailure.PortConflict, supervisedStart, timeout, ct).ConfigureAwait(false);
        }
        else
        {
            StartAttempt attempt = await StartCoreAsync(preferred, timeout, ct).ConfigureAwait(false);
            url = attempt.Url;
            if (url is null && preferred is not null && attempt.Failure is StartFailure failure && failure != StartFailure.Cancelled)
            {
                url = await RecoverFromFailureAsync(preferred.Value, failure, supervisedStart, timeout, ct).ConfigureAwait(false);
            }
        }

        if (url is not null)
        {
            _port = url.Port;
            // 跨进程持久化：冷启动复用同端口（origin 不变）才能恢复 dsh Web 端的上一会话
            PersistPort(url.Port);
            // 启动成功后收敛一次：抢端口输给我们的市场 helper/续任者此刻正等端口空出，就地收割。
            // 冷启动那次已在 spawn 前全量收敛，且此刻市场 UI 尚未起来，不重复扫描。
            if (!coldStart)
            {
                HarvestLineageResidue("启动成功后收敛");
            }
        }

        return url;
    }

    /// <summary>构造 dsh web 子进程的 ProcessStartInfo（PATH dsh 形态 + 环境注入）。
    /// 先剥离宿主继承噪声（ADR spawn-env-and-plugin-spec-hardening）再写我方变量。</summary>
    /// <param name="port">固定端口；<c>null</c> 时让 OS 分配（<c>--port 0</c>）。</param>
    /// <param name="home">共享 DSH_HOME。</param>
    /// <param name="spawnToken">孤儿清扫 token（注入环境变量，与落盘 pid 对应）。</param>
    internal static ProcessStartInfo BuildStartPsi(int? port, string home, string spawnToken)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        EnvironmentHygiene.StripInherited(psi);
        UseUtf8TextStreams(psi);
        psi.FileName = "dsh";

        foreach (string arg in BuildDshWebArgs(port))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["DSH_HOME"] = home;

        // GUI 会话的 PATH 不含 ~/.local/bin：MCP stdio 等下游按命令名拉取外部进程时
        // 解析不到用户级命令（ADR gui-path-enrichment）。追加而非前置，不改系统优先级。
        psi.Environment["PATH"] = BuildEnrichedPath(
            psi.Environment.TryGetValue("PATH", out string? currentPath) ? currentPath : null,
            home,
            Path.PathSeparator);

        // 血统 token（ADR self-update-exit-reaps-dsh-child 缺口 B；血统判据见 RuntimeLineage）：
        // 宿主异常死亡时 dsh 成 systemd 收养孤儿占端口。给本次 spawn 的 dsh 注入唯一 token（经环境变量），
        // 并把 pid+token 落盘；清扫时复验该 PID 进程环境带的 token。市场自重启的 helper 与续任者由 dsh
        // 以 `env: process.env` 转发同一变量，故同属血统——端口冲突时据此收养续任者或收割残留。
        psi.Environment[RuntimeLineage.TokenEnv] = spawnToken;
        return psi;
    }

    /// <summary>重启 dsh 子进程，返回新 URL。崩溃恢复用。</summary>
    /// <param name="timeout">等待新 URL 的时限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>与 <see cref="StartAsync"/> 同一串行化主体——Start 本就先 StopCore，此前的显式
    /// 双 Stop 是冗余；本方法保留为崩溃恢复路径的语义命名。</remarks>
    public Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        return StartAsync(timeout, ct);
    }

    /// <summary>当在管运行时退出时完成（用于崩溃监督；无在管运行时立即完成）。本进程子进程挂
    /// <c>Exited</c> 事件；收养的市场续任者非本进程子进程，改由 <see cref="WaitAdoptedExitAsync"/> 轮询判活。</summary>
    public Task WaitForExitAsync()
    {
        if (_process is not { HasExited: false } p)
        {
            return _adoptedPid is int adopted && _adoptedToken is string token
                ? WaitAdoptedExitAsync(adopted, token)
                : Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>停止并回收 dsh 子进程（整棵进程树）。经生命周期串行化门——门被 spawn 长时间
    /// 占用时记录并跳过，绝不无限阻塞退出路径；此窗口的残留 dsh 由冷启动孤儿清扫兜底
    /// （监督器已被 cancel，不会再有新的 spawn 与它竞争）。</summary>
    public void Stop()
    {
        if (!_lifecycleGate.Wait(TimeSpan.FromSeconds(3)))
        {
            _log?.Invoke("[host] Stop：生命周期门被占用（spawn 进行中？）跳过本次回收，残留交冷启动孤儿清扫兜底");
            return;
        }

        try
        {
            StopCore();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>门内的实际回收：kill 在管运行时（本进程子进程 + 收养的续任者）进程树，未退透留痕
    /// （残留交冷启动清扫与血统收割兜底）。</summary>
    private void StopCore()
    {
        if (_process is { HasExited: false } p)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程恰好已退出
            }

            if (!p.WaitForExit(3000))
            {
                // 观测位不是修复位：kill 已发出但未确认死亡，残留由冷启动清扫兜底
                _log?.Invoke($"[host] dsh（pid {p.Id}）kill 后 3s 未确认退出，残留交冷启动孤儿清扫兜底");
            }
        }

        _process = null;
        KillAdoptedRuntime();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
