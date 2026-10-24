namespace DeepSeek.Harness.Desktop.Core.Ui;

/// <summary>
/// 进入主界面用例（ADR 组合根机制收官 step-1 的 B1）：壳铸币 → 有界等窗 → 终页探针裁决 →
/// 鉴权页重铸并重载一次。原 <c>StartupSequence.EnterMainUi/SettleWebSession</c> 与导航原语（<c>Task.Run</c>
/// 同步原生调用隔离、有界超时、提交等待）整体下沉至此，逐句等价、零行为变更；下沉后整条链可经
/// <see cref="IWebViewNavigator"/> fake 单测，不再依赖真实窗口/进程。裁决策略在 <see cref="WebAuthRecovery"/>
/// （纯策略，本类只编排探针/导航/日志，R1），探针脚本与时限为构造数据。
/// </summary>
public sealed class MainUiEntry
{
    private readonly IWebViewNavigator _navigator;
    private readonly Func<DshWebUrl, Action<string>, CancellationToken, Task<bool>> _mintAsync;
    private readonly Uri? _proxyUrl;
    private readonly MainUiEntryOptions _options;
    private readonly string _probeScript;
    private readonly Action<string>? _log;

    /// <summary>创建进入主界面用例（构造数据注入；铸币经方法组接线到壳转发器，见组合点注册）。</summary>
    /// <param name="navigator">网页视图导航端口（Presentation 的 WebView 适配器）。</param>
    /// <param name="mintAsync">壳铸币（失败 loud 不抛，生产实现见 <c>DshShellForward.MintAsync</c>）。</param>
    /// <param name="proxyUrl">回环代理源根；null = 绑定失败（本次不导航，降级 wwwroot）。</param>
    /// <param name="options">时限与重试参数（配置模型的 Core 侧投影）。</param>
    /// <param name="probeScript">终页探针脚本（Presentation 资产，经构造数据交给用例）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public MainUiEntry(
        IWebViewNavigator navigator,
        Func<DshWebUrl, Action<string>, CancellationToken, Task<bool>> mintAsync,
        Uri? proxyUrl,
        MainUiEntryOptions options,
        string probeScript,
        Action<string>? log = null)
    {
        _navigator = navigator;
        _mintAsync = mintAsync;
        _proxyUrl = proxyUrl;
        _options = options;
        _probeScript = probeScript;
        _log = log;
    }

    /// <summary>引导完成后的铸币收尾（引导服务在 dsh 就位时回调）：覆盖式重铸（本路径是 bootstrap 的 epoch
    /// 起点），等窗后做终页裁决。<b>不主动导航</b>（鉴权自愈的同源重载除外）——窗口恒在代理源上，未铸币时
    /// holder 自轮询就绪后自 reload（renderer 发起，绕开原生 set_url 挂死家族；见 ADR loopback-forward-proxy）。</summary>
    /// <param name="url">dsh 就位端点（仅供落定重铸与日志；无需导航，恒驻代理源）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    public async Task RunAsync(DshWebUrl url, CancellationToken ct)
    {
        Uri? proxyUrl = _proxyUrl;
        if (proxyUrl is null)
        {
            Log("[nav] 进入主界面无代理源（回环绑定失败），本次不导航（dsh 已就绪，重启即进）");
            return;
        }

        // 壳铸币先行（不依赖窗口：纯 HTTP；holder 的就绪轮询以此次铸币为门——先铸才放行）。
        _ = await _mintAsync(url, Log, ct).ConfigureAwait(false);
        // 窗口可能尚未建好（原生建窗慢于 dsh 就位时，首个 Current 即抛，ADR bootstrap-window-ready-wait）：
        // 有界等可用，超时 loud 跳过本次（dsh 已就绪，holder 自 reload 即进）。
        if (!await WaitForWindowAsync(ct).ConfigureAwait(false))
        {
            Log($"[nav] 等窗口可用超时（{_options.WindowReadyTimeoutSeconds}s），跳过本次（holder 自 reload 即进）");
            return;
        }

        await SettleWebSessionAsync(url, proxyUrl, ct).ConfigureAwait(false);
    }

    /// <summary>网页会话落定（代理转发模型）：代理 URL 提交后探一次终页。鉴权页（代理链断/cookie 失效）则重铸
    /// 并重载代理页一次，再坏只 fail loud（不挡启动、不循环）。探针超时/异常按未知放过启动；
    /// 未知由冒烟见证门判定（与 Linux 同语义），启动不为此等待。</summary>
    /// <param name="url">dsh 就位端点（铸币用）。</param>
    /// <param name="proxyUrl">回环代理源根（<see cref="RunAsync"/> 已判非空并传入；收口重载的靶点）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task SettleWebSessionAsync(DshWebUrl url, Uri proxyUrl, CancellationToken ct)
    {
        string expectedOrigin = proxyUrl.GetLeftPart(UriPartial.Authority);
        string? sample = await ProbePageSampleAsync(ct).ConfigureAwait(false);
        WebAuthRecovery.PageVerdictDetail detail = WebAuthRecovery.ClassifyDetail(sample, expectedOrigin);
        if (detail.Verdict != WebAuthRecovery.PageVerdict.Auth)
        {
            LogPageVerdict(detail, expectedOrigin, string.Empty);
            return;
        }

        Log("[nav] 终页鉴权，重铸并重载代理页面（第 1 次）");
        _ = await _mintAsync(url, Log, ct).ConfigureAwait(false);
        await NavigateAndAwaitCommitAsync(proxyUrl, ct).ConfigureAwait(false);
        sample = await ProbePageSampleAsync(ct).ConfigureAwait(false);
        detail = WebAuthRecovery.ClassifyDetail(sample, expectedOrigin);
        LogPageVerdict(detail, expectedOrigin, "，重铸后");
    }

    /// <summary>有界导航调用：超时 loud 后返回（调用方沿「按已提交继续」走提交等待与探针）；
    /// 返回即记「调用已返回」；应用退出取消照常上抛。悬空池线程 fire-and-forget 可接受（探针先例）。</summary>
    /// <param name="target">导航靶点（日志仅记 authority，不记 query/token）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task NavigateWithTimeoutAsync(Uri target, CancellationToken ct)
    {
        try
        {
            await _navigator.NavigateAsync(target, ct)
                .WaitAsync(TimeSpan.FromSeconds(_options.NavCallTimeoutSeconds), ct).ConfigureAwait(false);
            Log($"[nav] 导航调用已返回：{target.GetLeftPart(UriPartial.Authority)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用退出：取消必须上抛（R2 B1），不吞。
            throw;
        }
        catch (TimeoutException)
        {
            Log($"[nav] 导航调用超时（{_options.NavCallTimeoutSeconds}s），按已提交继续");
        }
    }

    /// <summary>导航并等待其真正提交（引擎的「导航已到达」信号，先订阅后导航避免错过）。提交信号用于隔开
    /// 两跳导航——<c>NavigateAsync</c> 连发会被 WebKitGTK 合并，前一跳尚未发出即被后一跳覆盖。
    /// 等待超时按「已提交」降级继续（信号只是隔跳手段，缺位时不比单跳直导更差）；
    /// 调用本身亦有界（ADR navigate-call-timeout：arm64 实证原生调用可挂起，无界等即永卡）；
    /// 取消（应用退出）照常传播。</summary>
    /// <param name="target">导航靶点。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task NavigateAndAwaitCommitAsync(Uri target, CancellationToken ct)
    {
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigator.SetArrivalCallback(() => arrived.TrySetResult());
        try
        {
            Log($"[nav] 发起导航：{target.GetLeftPart(UriPartial.Authority)}");
            await NavigateWithTimeoutAsync(target, ct).ConfigureAwait(false);
            await WaitNavCommitAsync(arrived.Task, "导航", ct).ConfigureAwait(false);
        }
        finally
        {
            _navigator.SetArrivalCallback(static () => { });
        }
    }

    /// <summary>导航提交等待（有界；超时 loud 后按已提交继续）。<paramref name="hop"/> 记路名，
    /// 诊断时可分清哪条路走的。</summary>
    /// <param name="arrivedTask">提交信号任务。</param>
    /// <param name="hop">路名（日志前缀）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task WaitNavCommitAsync(Task arrivedTask, string hop, CancellationToken ct)
    {
        try
        {
            await arrivedTask.WaitAsync(TimeSpan.FromSeconds(_options.NavCommitTimeoutSeconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log($"[nav] {hop}提交等待超时（{_options.NavCommitTimeoutSeconds}s），按已提交继续");
        }
    }

    /// <summary>等主窗口可用：1s 轮询有界等；应用退出取消照常上抛，超时回 false（调用方 loud 跳过导航）。
    /// 原生 WebView 建窗可能慢于 dsh 就位（CI 无 D-Bus 会话实证 30s+），未就绪不再直接失败收口
    /// （ADR bootstrap-window-ready-wait）；时限在配置模型。</summary>
    /// <param name="ct">引导任务取消令牌。</param>
    /// <returns>窗口就绪 true；超时 false。</returns>
    private async Task<bool> WaitForWindowAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.WindowReadyTimeoutSeconds));
        while (true)
        {
            if (_navigator.IsWindowReady)
            {
                return true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.WindowReadyPollIntervalSeconds), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 应用退出：取消必须上抛（R2 B1），不吞。
                throw;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 终页裁决的唯一留痕点（ADR page-verdict-gate）：只记实际 origin 与文本长度，不记内容
    /// （防会话文本落盘；origin 不含 token 查询串）。冒烟门禁 grep 本行的
    /// <c>healthy</c>／<c>auth</c>／<c>unknown</c> 三态——一行一事实，绿即「同源且非鉴权页」。
    /// </summary>
    /// <param name="detail">Core 裁决明细（采样只拆一次，此处不重复拆）。</param>
    /// <param name="expectedOrigin">期望 origin（无采样时用于留痕）。</param>
    /// <param name="recovery">恢复动作标记（空串 = 无动作；如「重进后」）：只进日志尾巴，不参与裁决。</param>
    private void LogPageVerdict(WebAuthRecovery.PageVerdictDetail detail, string expectedOrigin, string recovery)
    {
        string where = detail.HasSample
            ? $"origin={detail.Origin} 可见文本 {detail.VisibleTextLength} 字"
            : $"探针无采样，期望 origin={expectedOrigin}";
        switch (detail.Verdict)
        {
            case WebAuthRecovery.PageVerdict.Healthy:
                Log($"[nav] 页面裁决={WebAuthRecovery.VerdictHealthy}（{where}{recovery}）");
                break;
            case WebAuthRecovery.PageVerdict.Auth:
                Log($"[nav] 页面裁决={WebAuthRecovery.VerdictAuth}（{where}{recovery}，请重开 dsh 打印的 URL；启动继续）");
                break;
            default:
                Log($"[nav] 页面裁决={WebAuthRecovery.VerdictUnknown}（{where}{recovery}）");
                break;
        }
    }

    /// <summary>探当前页采样（origin + 400 字可见文本，<see cref="PageProbeSample"/> 形态）；
    /// 有限重试后仍超时/异常返回 null（未知），调用方按放过启动处理（ADR page-verdict-gate，自 settle-gate-and-probe-retry 并入：
    /// 偶发 renderer 繁忙一次采样赌运气，成功即返，耗尽才 Unknown；快机器零变化）。</summary>
    /// <param name="ct">引导任务取消令牌。</param>
    /// <returns>采样文本；耗尽 null。</returns>
    private async Task<string?> ProbePageSampleAsync(CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.AuthProbeTimeoutSeconds));
            try
            {
                // 端口返回的是解码后的文本（桥回报 JSON 外壳由实现侧还原，否则 origin 带引号、同源恒不成立）。
                return await _navigator.EvaluateScriptAsync(_probeScript).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 应用退出：取消必须上抛（R2 B1），不吞。
                throw;
            }
            catch (OperationCanceledException) when (attempt >= _options.AuthProbeAttempts)
            {
                Log($"[nav] 鉴权探针超时（{_options.AuthProbeTimeoutSeconds}s×{attempt}次），跳过自愈检查");
                return null;
            }
            catch (OperationCanceledException)
            {
                Log($"[nav] 鉴权探针超时（{_options.AuthProbeTimeoutSeconds}s），第{attempt + 1}次重试");
            }
            catch (Exception ex) when (attempt >= _options.AuthProbeAttempts)
            {
                Log($"[nav] 鉴权探针失败（跳过自愈检查）：{ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Log($"[nav] 鉴权探针失败，第{attempt + 1}次重试：{ex.Message}");
            }
        }
    }

    /// <summary>日志出口（未注入即静默）。</summary>
    /// <param name="message">一行日志。</param>
    private void Log(string message) => _log?.Invoke(message);
}
