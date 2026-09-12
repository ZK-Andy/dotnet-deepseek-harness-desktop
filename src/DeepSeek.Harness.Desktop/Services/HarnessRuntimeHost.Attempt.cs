using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary><see cref="HarnessRuntimeHost"/> 的单次启动尝试面（partial）：StartCoreAsync/WaitForUrlAsync——
/// spawn 一次 dsh、等 URL、三路失败信号竞争与失败原因分类（ADR port-wait-compression）。</summary>
public sealed partial class HarnessRuntimeHost
{
    /// <summary>单次启动尝试的失败原因（决定首选端口冲突的后续处置与日志措辞）。</summary>
    private enum StartFailure
    {
        /// <summary>stderr 命中端口被占签名（EADDRINUSE）：签名时刻 = dsh 实际 bind 时刻（dsh 先加载整棵插件树，实测 42–47s 才到，
        /// ADR port-wait-compression——故另有 spawn 前的 bind 探测先拦），bind 失败后进程立即退出。</summary>
        PortConflict,

        /// <summary>子进程未给出 URL 即退出。</summary>
        EarlyExit,

        /// <summary>时限内未给出 URL。</summary>
        NoUrl,

        /// <summary>取消（终态，不再 spawn）。</summary>
        Cancelled,
    }

    /// <summary>单次启动尝试的结果：成功带 URL，失败带原因（null = 成功）。</summary>
    /// <param name="Url">成功时的 `dsh web:` URL。</param>
    /// <param name="Failure">失败原因；null 表示本次尝试成功。</param>
    private readonly record struct StartAttempt(Uri? Url, StartFailure? Failure)
    {
        /// <summary>成功结果。</summary>
        /// <param name="url">解析出的 URL。</param>
        public static StartAttempt Success(Uri url) => new(url, null);

        /// <summary>失败结果。</summary>
        /// <param name="failure">失败原因。</param>
        public static StartAttempt Failed(StartFailure failure) => new(null, failure);
    }

    /// <summary>单次启动尝试：spawn + 等 URL，失败即回收该次子进程（绝不留下无人认领的悬挂 dsh）。</summary>
    /// <param name="port">固定端口；<c>null</c> 时让 OS 分配。</param>
    /// <param name="timeout">等待 URL 的时限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功带 URL；失败带原因（端口冲突 / 早退 / 超时 / 取消）。</returns>
    private async Task<StartAttempt> StartCoreAsync(int? port, TimeSpan timeout, CancellationToken ct = default)
    {
        // 退出编排已取消：绝不 spawn 新子进程——否则孤儿 dsh 会越过 Stop 存活到壳死后，
        // 复现冷启动端口漂移（ADR child-process-reaping-port-drift）。取消是终态，按「起不来」返回。
        if (ct.IsCancellationRequested)
        {
            return StartAttempt.Failed(StartFailure.Cancelled);
        }

        string home = ResolveDshHome();
        Directory.CreateDirectory(home);
        string spawnToken = Guid.NewGuid().ToString("N");
        ProcessStartInfo psi = BuildStartPsi(port, home, spawnToken);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

        Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dsh 进程。");
        _process = process;
        // spawn 成功立即落盘 pid+token：崩溃监督重启（RestartAsync）复用同一路径覆盖为新 token。
        PersistSpawn(process.Id, spawnToken);
        if (ct.IsCancellationRequested)
        {
            // 取消落在上方检查点与 spawn 之间的窄窗：立即整树回收再返回，
            // 绝不让刚起的进程成为无人认领的孤儿（监督器此刻已在收摊，不会再 Stop 它）。
            // 门内语境：必须调 StopCore——公共 Stop 会因本方法已持有生命周期门而 3s 超时跳过。
            StopCore();
            return StartAttempt.Failed(StartFailure.Cancelled);
        }

        StartAttempt outcome = await WaitForUrlAsync(process, port, timeout, ct).ConfigureAwait(false);
        if (outcome.Url is not null)
        {
            // 起始时刻即「新生续任者」的参照：只有诞生于它之后的血统服务端才是接力产物
            _runtimeStartedUtc = startedUtc;
            return outcome;
        }

        // 失败尝试整树回收：它已不可能提供服务，留活只会变成无人认领的悬挂进程（bind 失败后 dsh 即自退）
        StopCore();
        return outcome;
    }

    /// <summary>等待本次尝试给出 URL：三条失败信号竞争，先到者胜。</summary>
    /// <param name="process">本次 spawn 的子进程。</param>
    /// <param name="port">本次尝试的固定端口（用于识别端口冲突签名）；<c>null</c> 时无签名可认。</param>
    /// <param name="timeout">等待 URL 的时限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功带 URL；失败带原因。</returns>
    /// <remarks>信号：①stderr 命中 EADDRINUSE 且点名该端口——签名时刻 = dsh 实际 bind 时刻（dsh 先加载整棵插件树
    /// 才 bind，实测 42–47s；bind 失败后进程立即退出，无尾部悬挂）；②子进程早退且未给出 URL；③时限耗尽/取消。</remarks>
    private async Task<StartAttempt> WaitForUrlAsync(Process process, int? port, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool portConflict = false;
        process.ErrorDataReceived += (_, e) =>
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

            if (port is int boundPort && RuntimeLineage.IsPortConflictStderr(e.Data, boundPort))
            {
                portConflict = true;
                tcs.TrySetResult(null);
            }
        };
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        process.OutputDataReceived += (_, e) =>
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
        process.BeginOutputReadLine();
        // 子进程早退（无 URL）即失败：不再空等满 timeout
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult(null);

        try
        {
            Uri? url = await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            if (url is not null)
            {
                return StartAttempt.Success(url);
            }

            return StartAttempt.Failed(portConflict ? StartFailure.PortConflict : StartFailure.EarlyExit);
        }
        catch (OperationCanceledException)
        {
            return StartAttempt.Failed(ct.IsCancellationRequested ? StartFailure.Cancelled : StartFailure.NoUrl);
        }
    }
}
