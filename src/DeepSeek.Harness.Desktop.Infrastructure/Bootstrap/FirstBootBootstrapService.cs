namespace DeepSeek.Harness.Desktop.Infrastructure.Bootstrap;

/// <summary>
/// 首启引导服务（R3 端口实现，ADR composition-root-value-flow-pipeline 批次 1）：全局 node/dsh 引导、
/// 随包插件装配、CLI shim、宿主启动的 dsh 进程交互面从装配面下沉为真类型服务；引导落定
/// TCS 私有化，对外只暴露类型化等待句柄；壳侧导航收尾经 <see cref="Start"/> 回调接回。
/// </summary>
public sealed class FirstBootBootstrapService : IFirstBootBootstrap
{
    private readonly Func<HarnessRuntimeHost> _host;
    private readonly IFirstBootUi _ui;
    private readonly Func<bool> _isEnglish;
    private readonly Action<string> _log;
    private RuntimeBootstrapOptions _options = new();
    private readonly RuntimeTimeouts _timeouts;
    private bool _needed;
    private string? _pathVersion;
    private TaskCompletionSource? _settled;
    private CancellationTokenSource? _cts;

    /// <summary>创建首启引导服务。</summary>
    /// <param name="host">运行时宿主提供者（宿主在引导服务之后构造，故为惰性委托）。</param>
    /// <param name="ui">引导页反馈端口（实现侧注入）。</param>
    /// <param name="isEnglish">失败文案是否取英文分支（惰性委托：宿主 UI 语言单点在装配面稍后构造，
    /// 引导任务启动时已就绪——与 <paramref name="host"/> 同一延迟捕获理由）。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="timeouts">运行时超时/重试配置（装配期唯一创建点传入，服务不再自行解析 appsettings）。</param>
    public FirstBootBootstrapService(
        Func<HarnessRuntimeHost> host,
        IFirstBootUi ui,
        Func<bool> isEnglish,
        RuntimeTimeouts timeouts,
        Action<string> log)
    {
        _host = host;
        _ui = ui;
        _isEnglish = isEnglish;
        _timeouts = timeouts;
        _log = log;
    }

    /// <inheritdoc />
    public bool IsNeeded => _needed;

    /// <inheritdoc />
    public string? ResolvedDshVersion => _pathVersion;

    /// <inheritdoc />
    public RuntimeBootstrapGate Gate { get; } = new();

    /// <inheritdoc />
    public void Resolve()
    {
        // 运行时来源（ADR simple-shell-single-global-dsh）：桌面是简单壳，依赖全机唯一的系统全局 node +
        // 全局 dsh（都在 PATH 上），桌面与终端共用同一套；没有系统 node 时由桌面装 node 到系统全局前缀
        //（需 sudo 则提示手动命令），而非桌面包私有运行时/私有 PATH。
        _options = RuntimeBootstrapOptions.Load(AppContext.BaseDirectory);
        // 若系统全局 node 已由桌面装好（此前安装/用户手动），把它暴露到进程 PATH，让宿主 spawn 与探测能解析。
        EnsureRuntimeNodeOnPath();

        // 启动只读探测 PATH 上全局 dsh——没有 → 走首启引导（npm install -g 装/更新到 @next）。
        // dev 判定只认显式环境标记（DSH_DESKTOP_RUNTIME_DIR / DSH_DESKTOP_DEV=1）——绝不以
        // 捆绑闭包存在性探测（打包新装同样没有闭包，探测会误判全部新装用户）。
        string? pathVersion = RuntimeVersionGate.ProbeAsync(
                TimeSpan.FromSeconds(_timeouts.VersionProbeTimeoutSeconds),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        _pathVersion = pathVersion;
        // 没有 → 装；落后 next 兼容底线（低于底部）→ 经引导更新到 @next；否则直接用。
        // 引导内 npm install -g @next 幂等（安装或更新）；检测"落后"以兼容底线（MinimumVersion）为
        // 廉价代理——精确对齐 @next 需启动时查询 npm dist-tag（见实现受阻点/决策点）。
        _needed = pathVersion is null || RuntimeVersionGate.IsBelowFloor(pathVersion);
        _log.Invoke(_needed
            ? $"[bootstrap] 全局 dsh 未检出或落后（{pathVersion ?? "(无)"}），进入首启引导"
            : $"[bootstrap] 全局 dsh 可用（{pathVersion}），跳过首启引导");
    }

    /// <inheritdoc />
    public void RegisterCliShim()
    {
        try
        {
            string? nodeBinDir = RuntimeBootstrap.TryResolveActiveNodeBinDir(_options);
            new CliShimRegistrar(_log).TryRegister(nodeBinDir);
        }
        catch (Exception ex)
        {
            // 注册是增强信息：任何未预期异常都不该打断启动链路
            _log.Invoke($"[cli-shim] 注册跳过（意外异常）：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Start(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady)
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），后台任务完成
        // 检测/下载/安装/验证状态机，成功后起 dsh 并把就位 URL 交回调接回装配面导航；失败推错误态等待
        // 用户重试（desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控。
        if (!_needed || _settled is not null)
        {
            return;
        }

        _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        CancellationToken bootCt = _cts.Token;
        _ = Task.Run(() => RunAsync(onRuntimeReady, bootCt));
    }

    /// <inheritdoc />
    public void Cancel() => _cts?.Cancel();

    /// <inheritdoc />
    public Task<bool> WaitSettledAsync(TimeSpan? timeout, CancellationToken ct) =>
        _settled is null
            ? Task.FromResult(true)
            : BootstrapSettleGate.WaitSettledAsync(_settled, timeout, ct);

    /// <summary>把"系统全局 node 的 global bin"暴露到进程 PATH（ADR simple-shell-single-global-dsh：无系统 node
    /// 时由桌面把 node 装到系统全局前缀，桌面与终端共用同一份）。node 已装到全局前缀时生效，否则 no-op。
    /// 两个调用点：构造期（node 已存在的复用场景）与引导装完 node 后（幂等补调——全新环境构造期
    /// 必然 no-op，就位后必须再调，后续装配探测/spawn 的裸名 pnpm 由此可解析，run `40137230061` 实证）。</summary>
    private void EnsureRuntimeNodeOnPath()
    {
        if (RuntimeBootstrap.TryResolveActiveNodeBinDir(_options) is { } nodeBin)
        {
            RuntimeBootstrap.PrependPathToProcessEnv(nodeBin);
            _log.Invoke($"[host] 系统全局 node 已暴露到 PATH：{nodeBin}");
        }
    }

    /// <summary>后台引导任务：重试循环 → 确保全局 dsh → 插件安装 → 起 dsh → 交回调导航进主界面；
    /// 任一失败收口为日志（窗口仍可重试/关闭）。</summary>
    private async Task RunAsync(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady, CancellationToken bootCt)
    {
        try
        {
            string? version = await RunBootstrapWithRetryAsync(bootCt);
            if (version is null)
            {
                _log.Invoke("[bootstrap] 引导未完成（用户放弃或应用退出）");
                return;
            }

            // 全局 dsh 就位：宿主以 PATH dsh 形态运行（ADR simple-shell-single-global-dsh）。
            _log.Invoke($"[bootstrap] 全局 dsh 就位：v{version}");

            // 引导装完 node 后补一次进程 PATH 暴露（幂等）：构造期的早调用在全新环境必然
            // no-op（node 未装），此刻就位才生效——后续装配探测/spawn 的裸名 pnpm 由此可解析。
            EnsureRuntimeNodeOnPath();

            // CLI shim 注册（ADR companion-pnpm-prerequisite）：清理历史 pnpm 转发 shim 残留 +
            // 暴露系统全局 node bin 到终端 PATH（pnpm 真身由装配前置经 npm 装到全局）。
            RegisterCliShim();

            bool pluginsInstalled = await InstallBootstrapPluginsAsync(
                RuntimeBootstrap.TryResolveActiveNodeBinDir(_options), bootCt);
            if (pluginsInstalled)
            {
                _log.Invoke("[host] 本轮有插件经事务管线换入 active（staged 体检已过，无需再探）");
            }

            HarnessRuntimeHost host = _host();
            Uri? url = await host.StartAsync(timeout: TimeSpan.FromSeconds(_timeouts.SpawnTimeoutSeconds), bootCt);
            if (url is null)
            {
                _log.Invoke($"[bootstrap] 引导完成但 dsh 未在时限内给出 URL。stderr 尾巴：\n{string.Join('\n', host.StderrTail.TakeLast(8))}");
                return;
            }

            _log.Invoke($"[host] runtime = {host.RuntimeDescription}");
            _log.Invoke($"[host] dsh web = {url}；从引导页导航进入主界面");
            await onRuntimeReady(DshWebUrl.From(url), bootCt);
        }
        catch (OperationCanceledException)
        {
            _log.Invoke("[bootstrap] 引导任务随应用退出取消");
        }
        catch (Exception ex)
        {
            // 后台引导任务的兜底收口：任何意外异常都不拖垮壳（窗口仍在，可重试或关闭）
            _log.Invoke($"[bootstrap] 引导任务意外失败：{ex.Message}");
        }
        finally
        {
            _settled!.TrySetResult();
        }
    }

    /// <summary>
    /// 引导重试循环：单次尝试（RuntimeBootstrap.RunAsync）→ 成功返回验证通过的全局 dsh 版本；
    /// 失败推错误态并等待重试信号（desktop.bootstrap.retry 经 <see cref="Gate"/> 放行）或应用退出
    /// （<paramref name="ct"/> 取消）。返回 null = 放弃（退出/取消）。
    /// </summary>
    private async Task<string?> RunBootstrapWithRetryAsync(CancellationToken ct)
    {
        RuntimeBootstrapOptions options = _options;
        int attempt = 0;

        while (true)
        {
            attempt++;
            Gate.Reset();
            // hooks 逐次构造：用户在失败等待期切换语言后重试，hooks 层文案与页面同取最新 locale
            RuntimeBootstrapHooks hooks = RuntimeBootstrap.CreateDefaultHooks(_log, _isEnglish(), options);
            // ②冒烟信号契约：`引导开始：` 子串必须保留（smoke-install-*.sh 的 BOOT_RE 锚定它）。
            _log.Invoke($"[bootstrap] 引导开始：第 {attempt} 次尝试，dshSpec={options.DshSpec}（用系统全局 node 的 npm 装到全局）");
            var attemptTimer = System.Diagnostics.Stopwatch.StartNew();
            BootstrapOutcome outcome;
            try
            {
                outcome = await RuntimeBootstrap.RunAsync(
                    options,
                    progress =>
                    {
                        // 步骤锚点同步记 host.log：冒烟心跳与人肉定位"卡在哪一步"读它，不读 UI。
                        _log.Invoke($"[bootstrap] 步骤 {progress.Step}：{progress.Message}");
                        _ = _ui.BootstrapStepAsync(progress.Step, progress.Message, progress.Failed);
                    },
                    hooks,
                    _isEnglish(),
                    ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (outcome.Success && outcome.DshVersion is { } version)
            {
                _log.Invoke($"[bootstrap] 第 {attempt} 次尝试成功（耗时 {attemptTimer.Elapsed.TotalSeconds:0}s）");
                return version;
            }

            string reason = outcome.Error ?? UiCopy.BootstrapUnknownError(_isEnglish());
            _log.Invoke($"[bootstrap] 引导失败（第 {attempt} 次，耗时 {attemptTimer.Elapsed.TotalSeconds:0}s）：{reason}（等待用户重试或退出）");
            // 推实际失败步骤：进度页据此红色高亮失败环节（推 "Ready" 会让高亮不可达）
            await _ui.BootstrapStepAsync(outcome.Step, reason, failed: true);

            // 等重试信号或应用退出；信号与取消都是即时语义事件，200ms 轮询足够。
            // 取消在此抛出 OCE（不吞）——由 RunAsync 的取消分支收口同一日志语义。
            while (!Gate.IsSignaled && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.RetryPollIntervalMilliseconds), ct);
            }

            if (ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>引导刚落定后的随包插件装配（ADR reference-alignment 批次一）：companion（internal）spawn 前
    /// 静默自愈。dsh 已在全局 PATH，nodeExe/dshEntry 双 null → 走 PATH dsh 命令。best-effort：失败只告警
    /// 不阻断启动。返回本次是否确有插件装成功（引导日志据此留痕）。</summary>
    private async Task<bool> InstallBootstrapPluginsAsync(string? runtimeNodeBinDir, CancellationToken bootCt)
    {
        // 对齐参照：companion（internal）在 spawn dsh 前静默自愈（batch-1）；best-effort：失败只告警
        // 不阻断（缺 companion 不阻塞 dsh 起动，下次启动自愈）。
        try
        {
            bool installed = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                nodeExe: null,
                dshEntry: null,
                HarnessRuntimeHost.ResolveDshHome(),
                Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                _log,
                PluginProcessRunner.RunAsync,
                (psi, ct) => PluginProcessRunner.RunProbeAsync(
                    psi, TimeSpan.FromSeconds(_timeouts.SpawnTimeoutSeconds), ct),
                bootCt,
                runtimeNodeBinDir).ConfigureAwait(false);
            // 随包驱动自带 staged 探针 + journal 换入（ADR transactional-plugin-pipeline）：换入前已证明
            // 能活，启动前的 active 事后探针随其 reconcile 分支退役。
            return installed;
        }
        catch (Exception ex)
        {
            _log.Invoke($"[host] 引导：随包插件安装失败（跳过）：{ex.Message}");
            return false;
        }
    }
}
