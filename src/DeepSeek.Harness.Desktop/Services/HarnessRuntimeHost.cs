using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>托管 dsh 运行时子进程：spawn dsh（`--profile desktop --port 0`），解析 `dsh web:` URL，管理生命周期。
/// 静态路径/环境解析面见 <c>HarnessRuntimeHost.Paths.cs</c>（partial）。</summary>
/// <remarks>对应 implemented architecture ADR shared-home-desktop-profile：壳只负责运行时生命周期，组合的 Harness
/// 插件树即应用运行时；产品态默认上游规范共享 home `~/.dsh`，专属 `profiles/desktop` 承载插件装配。
/// 支持捆绑运行时（<paramref name="bundled"/> 指定 node + dsh bin.js）；为 null 时回退 PATH 里的 dsh。</remarks>
public sealed partial class HarnessRuntimeHost : IDisposable
{
    private const int StderrTailCapacity = 40;

    private (string NodeExe, string DshEntry)? _bundled;
    private readonly Action<string>? _log;
    private int? _port;
    private Process? _process;

    /// <summary>Start/Stop/Restart 生命周期串行化门（防并发双 spawn/_process 覆盖互踩）。</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private readonly List<string> _stderrTail = new(StderrTailCapacity);
    private readonly object _stderrLock = new();

    /// <summary>创建运行时宿主。</summary>
    /// <param name="bundled">捆绑运行时 (node 可执行, dsh bin.js)；null 表示用 PATH 的 dsh。</param>
    /// <param name="log">日志回调（可选）：端口漂移等运行时决策留痕 host.log（缺省 null 安全）。</param>
    public HarnessRuntimeHost((string NodeExe, string DshEntry)? bundled = null, Action<string>? log = null)
    {
        _bundled = bundled;
        _log = log;
    }

    /// <summary>
    /// 首启引导成功后绑定运行时（ADR online-first-unbundled-runtime）：宿主以 null 构造、
    /// 引导完成后补绑，随后 StartAsync 按 bundled 形态 spawn。尚未启动过 dsh（_process 为空）
    /// 才允许绑定；已启动后补绑属调用序错误，fail loud。
    /// </summary>
    public void BindRuntime((string NodeExe, string DshEntry) runtime)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("dsh 已启动，禁止补绑运行时（调用序错误）");
        }

        _bundled = runtime;
        _log?.Invoke($"[host] 引导运行时已绑定：node={runtime.NodeExe} bin={runtime.DshEntry}");
    }

    /// <summary>本次采用的运行时描述（日志/恢复屏用）。</summary>
    public string RuntimeDescription =>
        _bundled is { } b ? $"bundled node={b.NodeExe} bin={b.DshEntry}" : "PATH dsh";

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

    /// <summary>StartAsync 的门内主体（Stop 之后的部分）：冷启动清扫 + spawn + 端口记忆。</summary>
    private async Task<Uri?> StartInnerAsync(TimeSpan timeout, CancellationToken ct)
    {
        // 冷启动（_port 未初始化）时清扫上次宿主异常死亡遗留的孤儿 dsh（ADR self-update-exit-reaps-dsh-child
        // 缺口 B）。仅冷启动做：进程内崩溃恢复（RestartAsync）时 _port 已设、PID 记录已被本次 spawn
        // 覆盖为新 token，重复清扫反而可能误评。token 复验不匹配/读不到一律不杀（零误杀）。
        if (_port is null)
        {
            _log?.Invoke($"[host] 冷启动：清扫孤儿 dsh（{ResolvePidFilePath()}）");
            OrphanDshReaper.Reap(
                ResolvePidFilePath(),
                OrphanDshReaper.ReadTokenLinux(),
                OrphanDshReaper.KillTreeProcessTree(),
                _log);
        }

        int? preferred = _port ?? TryLoadPersistedPort();
        Uri? url = await StartCoreAsync(preferred, timeout, ct);
        if (url is null && preferred is not null)
        {
            // 固定端口被占（kill 后未及时释放 / 其他进程占用）：回退 OS 分配
            url = await StartCoreAsync(null, timeout, ct);
            if (url is not null)
            {
                // 漂移告警（ADR child-process-reaping-port-drift）：观测位不是修复位——
                // origin 变化意味着上一会话选中态不保留，日志给出人可判读的残留信号
                _log?.Invoke($"[host] 首选端口 {preferred} 被占（疑似残留实例或孤儿 dsh），本次漂移至 {url.Port}；上一会话选中态将不保留");
            }
        }

        if (url is not null)
        {
            _port = url.Port;
            // 跨进程持久化：冷启动复用同端口（origin 不变）才能恢复 dsh Web 端的上一会话
            PersistPort(url.Port);
        }

        return url;
    }

    /// <summary>构造 dsh web 子进程的 ProcessStartInfo（bundled vs PATH dsh 形态 + 环境注入）。</summary>
    /// <param name="port">固定端口；<c>null</c> 时让 OS 分配（<c>--port 0</c>）。</param>
    /// <param name="home">共享 DSH_HOME。</param>
    /// <param name="spawnToken">孤儿清扫 token（注入环境变量，与落盘 pid 对应）。</param>
    private ProcessStartInfo BuildStartPsi(int? port, string home, string spawnToken)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        UseUtf8TextStreams(psi);
        if (_bundled is { } b)
        {
            psi.FileName = RuntimeBootstrap.StripExtendedPrefix(b.NodeExe);
            psi.ArgumentList.Add(b.DshEntry);
        }
        else
        {
            psi.FileName = "dsh";
        }

        foreach (string arg in BuildDshWebArgs(port))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["DSH_HOME"] = home;

        // 桌面环境可能 /home 只读：把 pnpm store/cache 重定向到可写的 DSH_HOME 下，
        // 否则 dsh-market 等插件安装走 pnpm 会因 store 写入失败（EROFS）而失败。
        ApplyPnpmWriteDirs(psi, home);

        // GUI 会话的 PATH 不含 ~/.local/bin：MCP stdio 等下游按命令名拉取外部进程时
        // 解析不到用户级命令（ADR gui-path-enrichment）。追加而非前置，不改系统优先级。
        psi.Environment["PATH"] = BuildEnrichedPath(
            psi.Environment.TryGetValue("PATH", out string? currentPath) ? currentPath : null,
            home,
            Path.PathSeparator);

        // 孤儿清扫 token（ADR self-update-exit-reaps-dsh-child，缺口 B）：宿主异常死亡时 dsh 成
        // systemd 收养孤儿占端口。给本次 spawn 的 dsh 注入唯一 token（经环境变量），并把 pid+token
        // 落盘；下次冷启动清扫时靠 /proc/<pid>/environ 复验 token 才杀——PID 复用指向无关进程时
        // 读不到本 token，绝不误杀。跨平台可测核心见 <see cref="OrphanDshReaper"/>。
        psi.Environment["DSH_DESKTOP_SPAWN_TOKEN"] = spawnToken;
        return psi;
    }

    private async Task<Uri?> StartCoreAsync(int? port, TimeSpan timeout, CancellationToken ct = default)
    {
        // 退出编排已取消：绝不 spawn 新子进程——否则孤儿 dsh 会越过 Stop 存活到壳死后，
        // 复现冷启动端口漂移（ADR child-process-reaping-port-drift）。取消是终态，按「起不来」返回。
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        string home = ResolveDshHome();
        Directory.CreateDirectory(home);
        string spawnToken = Guid.NewGuid().ToString("N");
        ProcessStartInfo psi = BuildStartPsi(port, home, spawnToken);

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dsh 进程。");
        // spawn 成功立即落盘 pid+token：崩溃监督重启（RestartAsync）复用同一路径覆盖为新 token。
        PersistSpawn(_process.Id, spawnToken);
        if (ct.IsCancellationRequested)
        {
            // 取消落在上方检查点与 spawn 之间的窄窗：立即整树回收再返回，
            // 绝不让刚起的进程成为无人认领的孤儿（监督器此刻已在收摊，不会再 Stop 它）。
            // 门内语境：必须调 StopCore——公共 Stop 会因本方法已持有生命周期门而 3s 超时跳过。
            StopCore();
            return null;
        }

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderrLock)
            {
                _stderrTail.Add(e.Data);
                if (_stderrTail.Count > StderrTailCapacity)
                {
                    _stderrTail.RemoveRange(0, _stderrTail.Count - StderrTailCapacity);
                }
            }
        };
        _process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tcs = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            Uri? uri = HarnessUrlParser.TryParse(e.Data);
            if (uri is not null)
            {
                tcs.TrySetResult(uri);
            }
        };
        _process.BeginOutputReadLine();

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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

    /// <summary>当 dsh 子进程退出时完成（用于崩溃监督；子进程不存在时立即完成）。</summary>
    public Task WaitForExitAsync()
    {
        if (_process is not { HasExited: false } p)
        {
            return Task.CompletedTask;
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

    /// <summary>门内的实际回收：kill 进程树 + 等 3s，未退透留痕（冷启动孤儿清扫兜底残留）。</summary>
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
                // 观测位不是修复位：kill 已发出但未确认死亡，残留由冷启动孤儿清扫兜底
                _log?.Invoke($"[host] dsh（pid {p.Id}）kill 后 3s 未确认退出，残留交冷启动孤儿清扫兜底");
            }
        }

        _process = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
