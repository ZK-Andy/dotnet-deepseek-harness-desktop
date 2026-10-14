using DeepSeek.Harness.Desktop.Core.Recovery;
using DeepSeek.Harness.Desktop.Core.Ui;

namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 运行时监督用例（ADR 组合根机制收官 step-1 的 B2）：引导落定门控 → <see cref="RuntimeSupervisor"/>
/// 监督循环 → 失败面承载（端口 <see cref="IRecoverySurface"/>）→ 恢复回调重验铸币态（端口
/// <see cref="IMintEpoch"/>）并按承载面决定是否补收口导航。原 <c>StartupSequence.SetupSupervisorAsync</c> 与
/// 失败面/恢复回调各段整体下沉（逐句等价：闩语义、模态不叠、回落面补导航），下沉后整条链可经 fake 端口单测。
/// 页面健康观测/顶栏注入/启动期告知/自更新检查不是监督语义，留在排序器各自调用（不造第二个上帝对象，R1）。
/// </summary>
public sealed class RuntimeSupervision
{
    private readonly IFirstBootBootstrap _bootstrap;
    private readonly IRecoverySurface _surface;
    private readonly IMintEpoch _mintEpoch;
    private readonly IWebViewNavigator _navigator;
    private readonly Uri? _proxyUrl;
    private readonly UiLocale _uiLocale;
    private readonly RuntimeSupervisionOptions _options;
    private readonly Action<string>? _log;
    private int _failureSurfaceShown; // 本恢复周期已弹过失败面（onRecovered 解锁）：连败不得按重试节拍刷模态
    private int _pageSurfaceFallbackShown; // 回落承载面覆写过活动页 DOM → 恢复回调须补收口导航
    private int _failureModalInFlight; // 已有未处置的原生模态在飞 → 不再叠新的（模态是阻塞原语，占池线程）

    /// <summary>创建监督用例（构造数据注入；宿主经 <see cref="RunAsync"/> 逐次收参——阶段产出仍在编排侧流动）。</summary>
    /// <param name="bootstrap">首启引导服务（落定握手句柄来源）。</param>
    /// <param name="surface">失败面承载端口（原生优先、页内回落）。</param>
    /// <param name="mintEpoch">壳铸币态端口（失效 + 收养重验）。</param>
    /// <param name="navigator">网页视图导航端口（回落承载面覆写 DOM 后的收口导航）。</param>
    /// <param name="proxyUrl">回环代理源根；null = 绑定失败（收口导航退化为不导航）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点（失败面原因/标题的文案分支）。</param>
    /// <param name="options">时限与留痕参数（配置模型的 Core 侧投影）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public RuntimeSupervision(
        IFirstBootBootstrap bootstrap,
        IRecoverySurface surface,
        IMintEpoch mintEpoch,
        IWebViewNavigator navigator,
        Uri? proxyUrl,
        UiLocale uiLocale,
        RuntimeSupervisionOptions options,
        Action<string>? log = null)
    {
        _bootstrap = bootstrap;
        _surface = surface;
        _mintEpoch = mintEpoch;
        _navigator = navigator;
        _proxyUrl = proxyUrl;
        _uiLocale = uiLocale;
        _options = options;
        _log = log;
    }

    /// <summary>启动监督任务（池线程）：等引导落定后进入监督循环，返回的任务供退出路径 join。
    /// 引导期门控是硬序——宿主尚无 dsh 进程时 <c>WaitForExitAsync</c> 立即完成，监督器会空转进恢复循环
    /// 并覆写引导页，必须等引导落定（成功 spawn 或确认放弃）才进入监视。</summary>
    /// <param name="host">运行时宿主（调用方创建并持有生命周期）。</param>
    /// <param name="ct">监督器取消令牌（寿命 = 本次 Run）。</param>
    /// <returns>监督任务（引导未落定即返回的短路任务亦经此 join）。</returns>
    public Task RunAsync(IRuntimeHost host, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var supervisor = new RuntimeSupervisor(
                host,
                restartTimeout: TimeSpan.FromSeconds(_options.RestartTimeoutSeconds),
                recoveredRetryDelay: TimeSpan.FromSeconds(_options.RecoveredRetryDelaySeconds),
                failedRetryDelay: TimeSpan.FromSeconds(_options.FailedRetryDelaySeconds),
                showRecovery: ShowFailureSurfaceAsync,
                onRecovered: OnRecoveredAsync,
                log: _log,
                blockedLogEveryRounds: _options.BlockedLogEveryRounds);
            if (!await _bootstrap.WaitSettledAsync(timeout: null, ct).ConfigureAwait(false))
            {
                return;
            }

            await supervisor.RunAsync(ct).ConfigureAwait(false);
        });

    /// <summary>失败面承载（ADR native-failure-surface）：原生优先（不碰 DOM），无工具环境回落页内恢复面；
    /// 起手失效铸币态（恢复周期起点）。</summary>
    /// <param name="isLockBlocked">残留锁死跳过重启（ADR residue-lock-fail-loud）：原因取锁文案，否则普通崩溃原因。</param>
    /// <returns>展示接线完成即完成的任务。</returns>
    private ValueTask ShowFailureSurfaceAsync(bool isLockBlocked)
    {
        if (_mintEpoch.Invalidate())
        {
            Log("[shell] dsh 进程退出：铸币态失效，holder 门控重新武装（cookie 保留待收养重验）");
        }

        // 原因选择是域决策（UiCopy.RecoveryReason）；尾部截断数是展示面（在承载实现侧）。
        string reason = UiCopy.RecoveryReason(isLockBlocked, _uiLocale.IsEnglish);
        if (_surface.HasNativeChannel)
        {
            // 一周期一次（闩在 onRecovered 解锁）；上一个模态未处置完则不再叠新的（模态是阻塞原语，会占池线程）。
            if (Interlocked.Exchange(ref _failureSurfaceShown, 1) == 0
                && Interlocked.CompareExchange(ref _failureModalInFlight, 1, 0) == 0)
            {
                _ = ShowNativeFailureAsync(reason);
            }

            return ValueTask.CompletedTask;
        }

        return ShowPageFailureSurfaceAsync(reason);
    }

    /// <summary>页内恢复面（回落承载面）：覆写活动页 DOM，故恢复后必须补一次收口导航（ADR recovery-navigation-gate-retirement Decision 3）。</summary>
    /// <param name="reason">人读失败原因。</param>
    /// <returns>注入接线完成即完成的任务。</returns>
    private ValueTask ShowPageFailureSurfaceAsync(string reason)
    {
        Volatile.Write(ref _pageSurfaceFallbackShown, 1);
        return _surface.ShowPageAsync(reason);
    }

    /// <summary>原生失败面（池线程，不阻塞监督循环）：退出意向单问、极性倒置（ADR native-failure-surface）——
    /// 破坏性动作只挂显式 true（通道故障恒回 false，落回继续恢复），退出动作由承载实现侧执行。</summary>
    /// <param name="reason">人读失败原因。</param>
    /// <returns>承载任务（调用方 fire-and-forget）。</returns>
    private async Task ShowNativeFailureAsync(string reason)
    {
        try
        {
            await _surface.ShowNativeAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 原生通道故障 → 回落页内恢复面：失败面绝不静默消失（ADR native-failure-surface Decision 2）
            Log($"[host] 原生失败面承载失败，回落页内恢复面：{ex.Message}");
            await ShowPageFailureSurfaceAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _failureModalInFlight, 0);
        }
    }

    /// <summary>恢复回调（监督器重启给出新 URL）：重验铸币态（epoch 可能已换，token-free 路径；ADR
    /// mint-epoch-mux-gate）并解锁失败面闩。<b>零壳导航的前提是承载面不碰活动页 DOM</b>——原生框不碰即不导航
    /// （窗口恒驻代理根，回来交页内自刷与客户端原地重连）；回落页内恢复面覆写过 DOM，只有该分支补收口导航。
    /// 重验在监督任务线程上 await（无同步上下文，不会死锁），不阻塞等 I/O。</summary>
    /// <param name="url">重启后的 dsh 端点（仅供重验与日志；导航靶点恒为代理根）。</param>
    /// <returns>重验与收口导航完成即完成的任务。</returns>
    private async ValueTask OnRecoveredAsync(Uri url)
    {
        await _mintEpoch.RevalidateAsync(DshWebUrl.From(url), CancellationToken.None).ConfigureAwait(false);
        Volatile.Write(ref _failureSurfaceShown, 0);
        Uri? target = _proxyUrl;
        if (Interlocked.Exchange(ref _pageSurfaceFallbackShown, 0) == 0 || target is null)
        {
            Log($"[nav] 恢复后重验铸币态（{url.GetLeftPart(UriPartial.Authority)}），不代发窗口导航");
            return;
        }

        Log($"[nav] 回落承载面覆写过活动页，收口回代理根（{url.GetLeftPart(UriPartial.Authority)}）");
        await _navigator.NavigateAsync(target, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>日志出口（未注入即静默）。</summary>
    /// <param name="message">一行日志。</param>
    private void Log(string message) => _log?.Invoke(message);
}
