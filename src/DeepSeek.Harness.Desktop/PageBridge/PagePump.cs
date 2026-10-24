using System.Text.Json;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 页面注入辅助（ADR 装配面只装配）：把 JS 注入类操作（横幅/引导进度/自更新状态）
/// 从装配面抽出为单点。窗口面经 <see cref="IWindowScriptTarget"/> 端口（Avalonia 适配器见
/// <see cref="AvaloniaWindowScriptTarget"/>，ADR 2026-10-22-avalonia-g2-shell-bridge）；
/// 未就绪的重试/丢弃语义在本类方法内。
/// 重试预算来自构造注入的配置（ADR 组合根寿命与配置单一来源 D4：预算由构造注入，不自行解析 appsettings）。
/// </summary>
internal sealed class PagePump
{
    private readonly RuntimeTimeouts _timeouts;

    /// <summary>横幅重试节拍（秒键的 TimeSpan 形态单点，免各调用点各转一次）。</summary>
    private readonly TimeSpan _bannerRetryDelay;

    /// <summary>注入/推送的重试节拍（毫秒键的 TimeSpan 形态单点）。</summary>
    private readonly TimeSpan _pushRetryDelay;

    /// <summary>创建页面注入单点。</summary>
    /// <param name="timeouts">运行时超时/重试配置（容器之前创建的唯一实例）。</param>
    public PagePump(RuntimeTimeouts timeouts)
    {
        _timeouts = timeouts;
        _bannerRetryDelay = TimeSpan.FromSeconds(timeouts.BannerRetryDelaySeconds);
        _pushRetryDelay = TimeSpan.FromMilliseconds(timeouts.PushRetryDelayMilliseconds);
    }

    /// <summary>窗口就绪且当前文档已是壳真 UI（不是 holder 过渡页）时注入横幅：
    /// holder 页在铸币后 <c>location.reload()</c> 进真 UI，注入到 holder 的横幅会随文档销毁消失
    /// （ADR avalonia-g3-update-surface 的结论横幅落点判据）。未就绪、holder 与文档未解析三态都按同一节拍
    /// 重试；预算按窗口就绪时限折算（<c>WindowReadyTimeoutSeconds</c>，与同一启动面的就绪预算是同一把尺）。</summary>
    /// <param name="target">页面脚本求值端口。</param>
    /// <param name="script">要注入的横幅脚本。</param>
    /// <param name="holderMarker">holder 页标记（Core <c>WebAuthRecovery.HolderMarker</c>，与代理服务页同源）。</param>
    /// <param name="ct">取消令牌（监督器/退出）。</param>
    internal Task ShowBannerWhenShellReadyAsync(IWindowScriptTarget target, string script, string holderMarker, CancellationToken ct) =>
        ShowBannerCoreAsync(
            target,
            script,
            holderMarker,
            AttemptsForBudget(TimeSpan.FromSeconds(_timeouts.WindowReadyTimeoutSeconds), _bannerRetryDelay),
            ct);

    /// <summary>结论横幅注入的循环体（ADR pagepump-retry-helper-unification）：先探 holder 判据，
    /// 非 <c>ui</c> 一律按未就绪重试。</summary>
    /// <param name="target">页面脚本求值端口。</param>
    /// <param name="script">要注入的横幅脚本。</param>
    /// <param name="holderMarker">holder 页标记（Core <c>WebAuthRecovery.HolderMarker</c>）。</param>
    /// <param name="maxAttempts">尝试次数上限。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task ShowBannerCoreAsync(
        IWindowScriptTarget target,
        string script,
        string holderMarker,
        int maxAttempts,
        CancellationToken ct)
    {
        RetryOutcome outcome = await RetryWhenReadyAsync(
            async token =>
            {
                string raw = await target.EvaluateAsync(WebAuthProbe.HolderScript(holderMarker), token).ConfigureAwait(false);
                // 只有明确读到真 UI 才注入：holder、文档未解析完（unknown）与探针异常都按「未就绪」再等一拍
                if (!string.Equals(ScriptProbeValue.Decode(raw), "ui", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("not-shell-ui");
                }

                await target.EvaluateAsync(script, token).ConfigureAwait(false);
            },
            ex => HostLog.Write($"[host] 横幅注入失败（放弃）：{ex.Message}"),
            maxAttempts,
            _bannerRetryDelay,
            ct).ConfigureAwait(false);
        if (outcome == RetryOutcome.Exhausted)
        {
            HostLog.Write($"[host] 结论横幅重试耗尽（{maxAttempts} 次内未等到壳真 UI，结论不再弹）");
        }
    }

    /// <summary>到点预算 → 尝试次数（ADR pagepump-retry-helper-unification）：不读墙钟，按节拍折算，
    /// 故单测确定可复现。到点非正值 = 该路停用（0 次尝试）；节拍非正值 = 预算不可度量，按单次尝试处理
    /// （防热循环）；商夹到 <see cref="int.MaxValue"/>（到点预算取 int 上限、节拍 1ms 时商可达 2.1e12）。</summary>
    /// <param name="deadline">到点预算。</param>
    /// <param name="retryDelay">重试节拍。</param>
    /// <returns>尝试次数。</returns>
    internal static int AttemptsForBudget(TimeSpan deadline, TimeSpan retryDelay)
    {
        if (deadline <= TimeSpan.Zero)
        {
            return 0;
        }

        if (retryDelay <= TimeSpan.Zero)
        {
            return 1;
        }

        return (int)Math.Min(int.MaxValue, Math.Ceiling(deadline.Ticks / (double)retryDelay.Ticks));
    }

    /// <summary>推一条引导进度到 wwwroot 引导页；未就绪按节拍重试至次数用尽，耗尽记日志放弃。</summary>
    internal async Task PushBootstrapStateAsync(IWindowScriptTarget target, string step, string message, bool failed)
    {
        // detail 必须是帧对象本身的 JSON（页面直接读 detail.step 等，无 JSON.parse）——
        // 与 PushUpdateState 的 state.ToJson() 同款形态，禁止二次包字符串
        string frameJson = JsonSerializer.Serialize(
            new BootstrapStateFrame(step, message, failed),
            AppJsonContext.Default.BootstrapStateFrame);
        string script = "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-bootstrap',{detail:"
            + frameJson
            + "}));}catch(e){}})();";
        RetryOutcome outcome = await RetryWhenReadyAsync(
            _ => target.EvaluateAsync(script, CancellationToken.None),
            ex => HostLog.Write($"[bootstrap] 进度推送失败（放弃）：{ex.Message}"),
            _timeouts.PushMaxAttempts,
            _pushRetryDelay,
            CancellationToken.None).ConfigureAwait(false);
        if (outcome == RetryOutcome.Exhausted)
        {
            HostLog.Write("[bootstrap] 进度推送重试耗尽（页面始终未就绪）");
        }
    }

    /// <summary>就绪前重试的单一循环骨架（ADR pagepump-retry-helper-unification）：注入/推送各路共用。
    /// 次数上限与节拍由调用点给（次数口径是配置键的既有语义）；到点预算只在横幅路出现，由
    /// <see cref="AttemptsForBudget"/> 单点折算成次数后同样以次数入参——绕开「次数 × 节拍 → TimeSpan」往返
    /// （节拍为 0 时该往返会把次数塌成 0）。</summary>
    /// <param name="op">单次尝试；抛 <see cref="InvalidOperationException"/> 视为「尚未就绪」。</param>
    /// <param name="onGiveUp">其余异常的一次性放弃留痕（场景前缀由调用点给）。</param>
    /// <param name="maxAttempts">尝试次数上限（≤0 即不尝试，直接进入耗尽终态）。</param>
    /// <param name="retryDelay">重试节拍（非正值即不等待，尝试背靠背）。</param>
    /// <param name="ct">取消令牌（监督器终止/退出路径）。</param>
    /// <returns>终态：成功 / 次数用尽 / 终止（已销毁、其余异常或取消）。</returns>
    private static async Task<RetryOutcome> RetryWhenReadyAsync(
        Func<CancellationToken, Task> op,
        Action<Exception> onGiveUp,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < maxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await op(ct).ConfigureAwait(false);
                return RetryOutcome.Succeeded;
            }
            catch (ObjectDisposedException)
            {
                // 退出期已销毁（本异常是 InvalidOperationException 的子类，须先于它捕获）：不再补尝试
                return RetryOutcome.Stopped;
            }
            catch (InvalidOperationException)
            {
                // 尚未就绪：计入一次节拍后重试
            }
            catch (Exception ex)
            {
                onGiveUp(ex);
                return RetryOutcome.Stopped;
            }

            if (attempt + 1 >= maxAttempts)
            {
                break;
            }

            try
            {
                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return RetryOutcome.Stopped;
            }
        }

        return ct.IsCancellationRequested ? RetryOutcome.Stopped : RetryOutcome.Exhausted;
    }

    /// <summary>重试单点的终态（调用点只区分「成功」与「次数用尽」两种需留痕的结局）。</summary>
    private enum RetryOutcome
    {
        /// <summary>尝试成功（调用点据此按需留取证行）。</summary>
        Succeeded,

        /// <summary>次数用尽而仍未就绪（调用点据此留「重试耗尽」行；<c>maxAttempts ≤ 0</c> 的配置形态也落此）。</summary>
        Exhausted,

        /// <summary>终止且不尝试补：退出期已销毁、其余异常（已由 <c>onGiveUp</c> 留痕）或取消——三者对调用点
        /// 不可区分，均不再记行（退出期常规路径，逐次留痕是噪音）。</summary>
        Stopped,
    }

    /// <summary>把自更新状态变化推给页面：插件监听 <c>dsh-desktop-update</c> CustomEvent 渲染更新按钮。
    /// 窗口未就绪即丢弃（不重试）——自更新状态机每次变化都会经 onTransition 再推，无需逐次送达。
    /// 保留 <see langword="static"/>，不降级为带重试的推送（自更新靠状态机后续变化补送）。</summary>
    internal static void PushUpdateState(IWindowScriptTarget target, UpdateState state)
    {
        try
        {
            _ = target.EvaluateAsync(
                "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-update',{detail:"
                + state.ToJson()
                + "}));}catch(e){}})();", CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未就绪：本次推送丢弃，后续状态变化会再推
        }
    }
}
