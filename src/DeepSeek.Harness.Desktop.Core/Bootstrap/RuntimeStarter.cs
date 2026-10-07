namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 运行时起步用例（ADR 组合根机制收官先行批）：首启 shim 注册 → 宿主 spawn 等 URL → 壳铸币。
/// 原 <c>StartupSequence.StartRuntime</c> 的逐句下沉（零行为变更）；下沉后该链可经 fake 宿主/铸币单测，
/// 不再依赖真实 dsh 进程。对标 <see cref="Update.UpdateCoordinator"/>：用例住 Core，
/// 纯端口（<see cref="IFirstBootBootstrap"/>/<see cref="IRuntimeHost"/>）+ BCL 委托注入，
/// 程序集零外层引用由 <c>ArchitectureTests</c> 互证；Infrastructure 适配（真实铸币）由组合点以方法组接入。
/// 同步阻塞（<c>GetAwaiter</c>）留调用链顶端，待 Ryn 线程模型审查后随 Run 链 async 化消除。
/// </summary>
public sealed class RuntimeStarter : IRuntimeStarter
{
    private readonly IFirstBootBootstrap _bootstrap;
    private readonly TimeSpan _spawnTimeout;
    private readonly Func<DshWebUrl, Action<string>, CancellationToken, Task<bool>> _mintAsync;
    private readonly Action<string>? _log;

    /// <summary>创建起步用例（构造数据注入；宿主由调用方创建持有，见 <see cref="IRuntimeStarter"/>）。</summary>
    /// <param name="bootstrap">首启引导服务（shim 注册与引导态判定）。</param>
    /// <param name="spawnTimeout">单次 spawn 等待 URL 的时限（<c>RuntimeTimeouts.SpawnTimeoutSeconds</c> 快照）。</param>
    /// <param name="mintAsync">壳铸币（失败 loud 不抛，生产实现见 <c>DshShellForward.MintAsync</c>）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public RuntimeStarter(
        IFirstBootBootstrap bootstrap,
        TimeSpan spawnTimeout,
        Func<DshWebUrl, Action<string>, CancellationToken, Task<bool>> mintAsync,
        Action<string>? log = null)
    {
        _bootstrap = bootstrap;
        _spawnTimeout = spawnTimeout;
        _mintAsync = mintAsync;
        _log = log;
    }

    /// <inheritdoc />
    public async Task<DshWebUrl?> StartAsync(IRuntimeHost host, CancellationToken ct)
    {
        // CLI shim 注册（ADR simple-shell-single-global-dsh）：dsh 已全局在 PATH，仅注册 pnpm shim。
        // best-effort——注册内部吞预期异常（见 CliShimRegistrar），此处再兜底意外异常。
        _bootstrap.RegisterCliShim();

        DshWebUrl? webUrl = _bootstrap.IsNeeded
            ? null
            : DshWebUrl.FromNullable(await host.StartAsync(_spawnTimeout, ct).ConfigureAwait(false));
        if (!_bootstrap.IsNeeded)
        {
            _log?.Invoke($"[host] runtime = {host.RuntimeDescription}");
            if (webUrl is not null)
            {
                _log?.Invoke($"[host] dsh web = {webUrl}");
            }
            else
            {
                _log?.Invoke($"[host] dsh 未在时限内给出 URL；降级加载 wwwroot。stderr 尾巴：\n{string.Join('\n', host.StderrTail.TakeLast(8))}");
            }
        }

        if (webUrl is not null)
        {
            // 壳铸币（对齐上游 authenticateWebHost）：窗口/导航一律走壳 origin，先铸后载；
            // 失败 loud，窗口照开（转发 401/502 → 探针/恢复面按错误页处理，不挡启动）。
            _ = await _mintAsync(webUrl.Value, _log ?? DropLog, ct).ConfigureAwait(false);
        }

        // dsh web URL 就此收官：下游（监督器/收养导航）各自从宿主或回调取得 URL，
        // 无跨阶段消费点——调用方不设阶段产出（死载荷会被 CompositionRootSequenceTests 拦下）。
        return webUrl;
    }

    /// <summary>空日志接收器（未注入日志回调时的铸币日志出口）。</summary>
    /// <param name="message">忽略的消息。</param>
    private static void DropLog(string message)
    {
    }
}
