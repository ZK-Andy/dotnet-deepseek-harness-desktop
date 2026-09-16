using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Bootstrap;

/// <summary>
/// 首启引导服务（R3 端口实现，ADR composition-root-value-flow-pipeline 批次 1）：全局 node/dsh 引导、
/// 随包与可选插件装配、CLI shim、宿主启动的 dsh 进程交互面从组合根下沉为真类型服务；引导落定
/// TCS 私有化，对外只暴露类型化等待句柄；壳侧导航收尾经 <see cref="Start"/> 回调接回。
/// </summary>
public sealed class FirstBootBootstrapService : IFirstBootBootstrap
{
    private readonly Func<HarnessRuntimeHost> _host;
    private readonly IFirstBootUi _ui;
    private readonly Func<bool> _isEnglish;
    private readonly Action<string> _log;
    private RuntimeBootstrapOptions _options = new();
    private bool _needed;
    private TaskCompletionSource? _settled;
    private CancellationTokenSource? _cts;

    /// <summary>创建首启引导服务。</summary>
    /// <param name="host">运行时宿主提供者（宿主在引导服务之后构造，故为惰性委托）。</param>
    /// <param name="ui">引导页反馈端口（实现侧注入）。</param>
    /// <param name="isEnglish">失败文案是否取英文分支（惰性委托：宿主 UI 语言单点在组合根稍后构造，
    /// 引导任务启动时已就绪——与 <paramref name="host"/> 同一延迟捕获理由）。</param>
    /// <param name="log">日志回调。</param>
    public FirstBootBootstrapService(Func<HarnessRuntimeHost> host, IFirstBootUi ui, Func<bool> isEnglish, Action<string> log)
    {
        _host = host;
        _ui = ui;
        _isEnglish = isEnglish;
        _log = log;
    }

    /// <inheritdoc />
    public bool IsNeeded => _needed;

    /// <inheritdoc />
    public RuntimeBootstrapGate Gate { get; } = new();

    /// <inheritdoc />
    public PreinstallChoiceGate PreinstallGate { get; } = new();

    /// <inheritdoc />
    public void Resolve()
    {
        // 运行时来源（ADR simple-shell-single-global-dsh）：桌面是简单壳，依赖全机唯一的系统全局 node +
        // 全局 dsh（都在 PATH 上），桌面与终端共用同一套；没有系统 node 时由桌面装 node 到系统全局前缀
        //（需 sudo 则提示手动命令），而非桌面包私有运行时/私有 PATH。
        _options = RuntimeBootstrapOptions.Load(AppContext.BaseDirectory);
        // 若系统全局 node 已由桌面装好（此前安装/用户手动），把它暴露到进程 PATH，让宿主 spawn 与探测能解析。
        EnsureRuntimeNodeOnPath();

        // 启动只读探测 PATH 上全局 dsh——没有 → 走首启引导（npm install -g 装/更新到 @alpha）。
        // dev 判定只认显式环境标记（DSH_DESKTOP_RUNTIME_DIR / DSH_DESKTOP_DEV=1）——绝不以
        // 捆绑闭包存在性探测（打包新装同样没有闭包，探测会误判全部新装用户）。
        string? pathVersion = RuntimeVersionGate.ProbeAsync(CancellationToken.None)
            .GetAwaiter().GetResult();
        // 没有 → 装；落后 alpha 兼容底线（低于底部）→ 经引导更新到 @alpha；否则直接用。
        // 引导内 npm install -g @alpha 幂等（安装或更新）；检测"落后"以兼容底线（MinimumVersion）为
        // 廉价代理——精确对齐 @alpha 需启动时查询 npm dist-tag（见实现受阻点/决策点）。
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
        // 检测/下载/安装/验证状态机，成功后起 dsh 并把就位 URL 交回调接回组合根导航；失败推错误态等待
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
    /// 时由桌面把 node 装到系统全局前缀，桌面与终端共用同一份）。node 已装到全局前缀时生效，否则 no-op。</summary>
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

            // CLI shim 注册（dsh 已全局在 PATH；仅注册内容恒定的 pnpm shim）。
            RegisterCliShim();

            bool pluginsInstalled = await InstallBootstrapPluginsAsync(bootCt);
            if (pluginsInstalled)
            {
                _log.Invoke("[host] 本轮有插件经事务管线换入 active（staged 体检已过，无需再探）");
            }

            HarnessRuntimeHost host = _host();
            Uri? url = await host.StartAsync(timeout: TimeSpan.FromSeconds(60), bootCt);
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
        _log.Invoke($"[bootstrap] 引导开始：dshSpec={options.DshSpec}（用系统全局 node 的 npm 装到全局）");

        while (true)
        {
            Gate.Reset();
            // hooks 逐次构造：用户在失败等待期切换语言后重试，hooks 层文案与页面同取最新 locale
            RuntimeBootstrapHooks hooks = RuntimeBootstrap.CreateDefaultHooks(_log, _isEnglish());
            BootstrapOutcome outcome;
            try
            {
                outcome = await RuntimeBootstrap.RunAsync(
                    options,
                    progress => _ = _ui.BootstrapStepAsync(progress.Step, progress.Message, progress.Failed),
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
                return version;
            }

            string reason = outcome.Error ?? UiCopy.BootstrapUnknownError(_isEnglish());
            _log.Invoke($"[bootstrap] 引导失败：{reason}（等待用户重试或退出）");
            // 推实际失败步骤：进度页据此红色高亮失败环节（推 "Ready" 会让高亮不可达）
            await _ui.BootstrapStepAsync(outcome.Step, reason, failed: true);

            // 等重试信号或应用退出；信号与取消都是即时语义事件，200ms 轮询足够。
            // 取消在此抛出 OCE（不吞）——由 RunAsync 的取消分支收口同一日志语义。
            while (!Gate.IsSignaled && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }

            if (ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>引导刚落定后的插件装配（ADR reference-alignment 批次一/二）：companion（internal）spawn 前
    /// 静默自愈，dshmarket（preset）经引导页确认装/跳过。dsh 已在全局 PATH，nodeExe/dshEntry 双 null →
    /// 走 PATH dsh 命令。best-effort：失败只告警不阻断启动。返回本次是否确有插件装成功
    /// （体检探针的触发条件，ADR plugin-install-health-probe）。</summary>
    private async Task<bool> InstallBootstrapPluginsAsync(CancellationToken bootCt)
    {
        // 对齐参照：companion（internal）在 spawn dsh 前静默自愈（batch-1），不出现在
        // 引导勾选清单（对齐 ensure_internal_plugins）；best-effort：失败只告警不阻断
        // （缺 companion 不阻塞 dsh 起动，下次启动自愈）。
        bool bundledInstalled = false;
        try
        {
            bundledInstalled = await MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                nodeExe: null,
                dshEntry: null,
                HarnessRuntimeHost.ResolveDshHome(),
                Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                _log,
                PluginProcessRunner.RunAsync,
                PluginProcessRunner.RunProbeAsync,
                bootCt);
        }
        catch (Exception ex)
        {
            _log.Invoke($"[host] 引导：随包插件安装失败（跳过）：{ex.Message}");
        }

        // 首启插件引导（ADR reference-alignment 批次二）：dshmarket（preset）经引导页
        // chip 确认/跳过 + 日志回流；用户确认后才装（StartAsync 前，与 batch-1 合流）。
        // 跳过则该次不装（less-bootstrapped，dsh 起动后可从应用内市场/设置自愈补装）。
        try
        {
            await RunPreinstallPhaseAsync(bootCt);
        }
        catch (Exception ex)
        {
            _log.Invoke($"[host] 插件引导异常跳过：{ex.Message}");
        }

        // 事务管线（ADR transactional-plugin-pipeline）：市场驱动自带 staged 探针 + journal 换入，
        // 换入前已证明能活——两个安装驱动均已事务化，启动前的 active 事后探针随其 reconcile 分支一并退役。
        return bundledInstalled;
    }

    /// <summary>
    /// 首启插件引导相（ADR reference-alignment 批次二）：全局 dsh 就位后（StartAsync 前），
    /// 若存在待装可选插件（preset），引导页呈现 chip + 确认/跳过 + 日志回流；用户确认才安装，跳过则不装。
    /// 5 分钟无决策默认跳过（避免壳永久挂在安装前、dsh 永不启动；跳过可经应用内市场补装）。
    /// 安装走事务管线（ADR transactional-plugin-pipeline）：staged 探针 + journal 换入，自带体检。
    /// </summary>
    private async Task RunPreinstallPhaseAsync(CancellationToken ct)
    {
        string home = HarnessRuntimeHost.ResolveDshHome();
        string profileDir = Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName);
        string profilePkg = Path.Combine(profileDir, "package.json");
        List<string> pending = PresetPluginCatalog.PendingForFirstBoot(profilePkg, _log);
        if (pending.Count == 0)
        {
            _log.Invoke("[host] 插件引导：无可选插件待装，跳过");
            return;
        }

        PreinstallGate.Reset();
        // 步骤高亮：引导页把「插件准备」步点亮（renderBootstrap 按 step 序置 active）。
        // 步骤名经枚举派生（单一事实源），避免与 JS STEP_ORDER 漂移。
        await _ui.BootstrapStepAsync(BootstrapStep.PreinstallPlugins, UiCopy.PreinstallStepPreparing(_isEnglish()), failed: false);
        await _ui.PreinstallDecisionAsync(pending);
        _log.Invoke($"[host] 插件引导：呈现可选插件 {string.Join(", ", pending)}，等待用户决策（5 分钟超时默认跳过）");

        PreinstallChoice choice;
        try
        {
            choice = await PreinstallGate.Choice.WaitAsync(TimeSpan.FromMinutes(5), ct);
        }
        catch (TimeoutException)
        {
            _log.Invoke("[host] 插件引导等待用户决策超时（5 分钟），默认跳过（可从应用内市场补装）");
            choice = PreinstallChoice.Skip;
        }

        if (choice == PreinstallChoice.Skip)
        {
            _log.Invoke("[host] 插件引导：用户跳过，本次不安装可选插件");
            await _ui.PreinstallDoneAsync(PreinstallChoice.Skip, null, UiCopy.PreinstallSkippedMessage(_isEnglish()));
            await _ui.BootstrapStepAsync(BootstrapStep.Ready, UiCopy.PreinstallStepReady(_isEnglish()), failed: false);
            return;
        }

        _log.Invoke("[host] 插件引导：用户确认，开始安装可选插件");
        try
        {
            await _ui.PreinstallInstallingAsync(PresetPluginCatalog.Market);
            await MarketInstallHelper.EnsureMarketFromRegistryAsync(
                nodeExe: null,
                dshEntry: null,
                home,
                _log,
                RunDshPluginAddStreamingAsync,
                PluginProcessRunner.RunProbeAsync,
                ct);
            bool installed = MarketInstallHelper.IsBundleInstalled(profilePkg, PresetPluginCatalog.Market);
            await _ui.PreinstallDoneAsync(PreinstallChoice.Install, installed, installed ? UiCopy.PreinstallDoneMessage(_isEnglish()) : UiCopy.PreinstallFailedMessage(_isEnglish()));
            _log.Invoke($"[host] 插件引导：可选插件安装{(installed ? "成功" : "未成功")}（{PresetPluginCatalog.Market}）");
        }
        catch (Exception ex)
        {
            _log.Invoke($"[host] 插件安装异常：{ex.Message}");
            await _ui.PreinstallDoneAsync(PreinstallChoice.Install, false, ex.Message);
        }
        finally
        {
            // 步骤收尾：无论装/跳/失败，引导页把「插件准备」置 done 后再导航进主界面
            await _ui.BootstrapStepAsync(BootstrapStep.Ready, UiCopy.PreinstallStepReady(_isEnglish()), failed: false);
        }
    }

    /// <summary>流式执行器：把 <c>dsh plugin add</c> 的每行输出推给插件引导页日志区。
    /// 统一走 <see cref="PluginProcessRunner.RunStreamingAsync"/>——单一实现、含取消/异常整树击杀
    /// （对齐 <c>RuntimeBootstrap.RunCaptureAsync</c> 防御不变量），引导路径传 bootCt 取消时
    /// 不再让 dsh plugin add 带 profile 写权成孤儿。</summary>
    private async Task<(int Exit, string Out, string Err)> RunDshPluginAddStreamingAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        return await PluginProcessRunner.RunStreamingAsync(psi, ct, _ui.PreinstallLog);
    }
}
