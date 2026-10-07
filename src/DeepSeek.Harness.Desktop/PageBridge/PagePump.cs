using System.Text.Json;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 页面注入辅助（ADR 组合根只装配）：把 JS 注入类操作（横幅/引导进度/插件引导状态/日志回流/自更新状态）
/// 从组合根抽出为静态单点。均以 <see cref="CurrentWindowAccessor"/>（或就绪前会抛的
/// <see cref="IRynWebView"/> 惰性解析）为参数（未就绪的重试/丢弃语义在方法内），不依赖组合根实例状态。
/// </summary>
internal static class PagePump
{
    /// <summary>页面注入超时家（单例装载，见 <c>RuntimeTimeouts</c>）。</summary>
    private static readonly RuntimeTimeouts s_timeouts =
        RuntimeTimeouts.Load(AppContext.BaseDirectory, HostLog.Write);

    /// <summary>横幅重试节拍（秒键的 TimeSpan 形态单点，免各调用点各转一次）。</summary>
    private static readonly TimeSpan s_bannerRetryDelay = TimeSpan.FromSeconds(s_timeouts.BannerRetryDelaySeconds);

    /// <summary>注入/注册/推送的重试节拍（毫秒键的 TimeSpan 形态单点）。</summary>
    private static readonly TimeSpan s_pushRetryDelay = TimeSpan.FromMilliseconds(s_timeouts.PushRetryDelayMilliseconds);

    /// <summary>窗口就绪后注入横幅：Current 未就绪按节拍重试至次数用尽；其余异常记日志放弃——
    /// 横幅是增强告知，绝不拖垮启动链路。</summary>
    internal static async Task ShowBannerWhenReadyAsync(CurrentWindowAccessor accessor, string script, CancellationToken ct)
    {
        RetryOutcome outcome = await RetryWhenReadyAsync(
            _ => EvaluateAsync(accessor, script),
            ex => HostLog.Write($"[host] banner 注入失败：{ex.Message}"),
            s_timeouts.BannerMaxAttempts,
            s_bannerRetryDelay,
            ct).ConfigureAwait(false);
        if (outcome == RetryOutcome.Exhausted)
        {
            HostLog.Write($"[host] banner 注入重试耗尽（{s_timeouts.BannerMaxAttempts} 次后窗口仍未就绪）");
        }
    }

    /// <summary>窗口就绪后注入自绘顶栏（ADR frameless-uniform-caption-bar）：重试语义与横幅同源
    /// （未就绪按节拍重试至次数用尽，其余异常记日志放弃——顶栏属增强面，绝不拖垮启动/导航链路；
    /// 失败退路是托盘菜单的唤回/最大化/退出命令）。脚本幂等，导航后重注入无副作用。</summary>
    internal static async Task InjectCaptionBarWhenReadyAsync(CurrentWindowAccessor accessor, string script, CancellationToken ct)
    {
        RetryOutcome outcome = await RetryWhenReadyAsync(
            _ => EvaluateAsync(accessor, script),
            ex => HostLog.Write($"[host] caption-bar 注入失败：{ex.Message}"),
            s_timeouts.PushMaxAttempts,
            s_pushRetryDelay,
            ct).ConfigureAwait(false);
        if (outcome == RetryOutcome.Exhausted)
        {
            HostLog.Write($"[host] caption-bar 注入重试耗尽（{s_timeouts.PushMaxAttempts} 次后窗口仍未就绪）");
        }
    }

    /// <summary>注册「每次页面加载执行」的顶栏脚本（ADR frameless-uniform-caption-bar「注入时机」条）：
    /// 三路注入里<b>唯一不靠导航事件覆盖后续每次加载</b>的一路——holder 页 <c>location.reload()</c> 进真 UI 是<b>同 URL
    /// 重载</b>，mac/win 的 <c>navigated</c> 回调在 URL/Source 未变时不发，真 UI 文档会因此拿不到顶栏
    /// （Linux 用户脚本逐文档重放，故该端导航钩子也覆盖）。webview 未就绪（<c>DeferredRynWebView</c> 在
    /// RunAsync 前抛 InvalidOperationException）按节拍重试至<b>到点预算</b>折算出的次数
    /// （<see cref="RuntimeTimeouts.CaptionBarRegisterSeconds"/> 经 <see cref="AttemptsForBudget"/> 折算；
    /// mac x64/Rosetta 的 webview 创建晚于按次数计的 6s 预算，发版腿实证），其余异常留痕放弃——
    /// 顶栏属增强面，绝不拖垮启动链路。</summary>
    internal static Task RegisterCaptionBarScriptWhenReadyAsync(Func<IRynWebView> webView, string script, CancellationToken ct) =>
        RegisterCaptionBarScriptWhenReadyAsync(
            webView, script,
            AttemptsForBudget(TimeSpan.FromSeconds(s_timeouts.CaptionBarRegisterSeconds), s_pushRetryDelay),
            s_pushRetryDelay,
            ct);

    /// <summary>同上，次数预算可注入（单测覆盖「次数用尽」出口；生产走 <see cref="AttemptsForBudget"/> 折算）。</summary>
    internal static async Task RegisterCaptionBarScriptWhenReadyAsync(
        Func<IRynWebView> webView, string script, int maxAttempts, TimeSpan retryDelay, CancellationToken ct)
    {
        RetryOutcome outcome = await RetryWhenReadyAsync(
            token => RegisterScriptAsync(webView, script, token),
            ex => HostLog.Write($"[caption-bar] 每页加载脚本注册失败（放弃）：{ex.Message}"),
            maxAttempts,
            retryDelay,
            ct).ConfigureAwait(false);
        if (outcome == RetryOutcome.Succeeded)
        {
            HostLog.Write("[caption-bar] 每页加载脚本已注册（同 URL 重载不再依赖导航事件）");
        }
        else if (outcome == RetryOutcome.Exhausted)
        {
            HostLog.Write($"[caption-bar] 每页加载脚本注册重试耗尽（{maxAttempts} 次后网页视图仍未就绪）");
        }
    }

    /// <summary>到点预算 → 尝试次数（ADR pagepump-retry-helper-unification）：不读墙钟，按节拍折算，
    /// 故单测确定可复现。到点非正值 = 该路停用（0 次尝试）；节拍非正值 = 预算不可度量，按单次尝试处理
    /// （防热循环）；商夹到 <see cref="int.MaxValue"/>（<c>CaptionBarRegisterSeconds</c> 取 int 上限 +
    /// 1ms 节拍时商可达 2.1e12）。</summary>
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
    internal static async Task PushBootstrapStateAsync(CurrentWindowAccessor accessor, string step, string message, bool failed)
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
            _ => EvaluateAsync(accessor, script),
            ex => HostLog.Write($"[bootstrap] 进度推送失败（放弃）：{ex.Message}"),
            s_timeouts.PushMaxAttempts,
            s_pushRetryDelay,
            CancellationToken.None).ConfigureAwait(false);
        if (outcome == RetryOutcome.Exhausted)
        {
            HostLog.Write("[bootstrap] 进度推送重试耗尽（页面始终未就绪）");
        }
    }

    /// <summary>构建 <c>dsh-desktop-preinstall</c> CustomEvent 注入脚本（detail 为帧对象 JSON）。</summary>
    internal static string PreinstallEventScript(PreinstallFrame frame)
    {
        string frameJson = JsonSerializer.Serialize(frame, AppJsonContext.Default.PreinstallFrame);
        return "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-preinstall',{detail:"
            + frameJson
            + "}));}catch(e){}})();";
    }

    /// <summary>推送一条插件引导状态（decision/installing/done）到引导页，重试语义同 <see cref="PushBootstrapStateAsync"/>。</summary>
    internal static async Task RetryPushPreinstallAsync(CurrentWindowAccessor accessor, PreinstallFrame frame)
    {
        string script = PreinstallEventScript(frame);
        RetryOutcome outcome = await RetryWhenReadyAsync(
            _ => EvaluateAsync(accessor, script),
            ex => HostLog.Write($"[preinstall] 状态推送失败（放弃）：{ex.Message}"),
            s_timeouts.PushMaxAttempts,
            s_pushRetryDelay,
            CancellationToken.None).ConfigureAwait(false);
        if (outcome == RetryOutcome.Exhausted)
        {
            HostLog.Write("[preinstall] 状态推送重试耗尽（页面始终未就绪）");
        }
    }

    /// <summary>就绪前重试的单一循环骨架（ADR pagepump-retry-helper-unification）：注入/注册/推送四路共用。
    /// 次数上限与节拍由调用点给（次数口径是这三类配置键的既有语义）；到点预算只在注册路出现，由
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

    /// <summary>对当前窗口求值注入脚本（窗口未就绪时 Current 抛 InvalidOperationException，交重试单点）。</summary>
    private static Task EvaluateAsync(CurrentWindowAccessor accessor, string script) =>
        accessor.Current.EvaluateJavaScriptAsync(script).AsTask();

    /// <summary>向 webview 注册「每次页面加载执行」的脚本（未就绪时惰性解析抛 InvalidOperationException，交重试单点）。</summary>
    private static Task RegisterScriptAsync(Func<IRynWebView> webView, string script, CancellationToken ct) =>
        webView().InjectScriptAsync(script, ct).AsTask();

    /// <summary>重试单点的终态（调用点只区分「成功」与「次数用尽」两种需留痕的结局）。</summary>
    private enum RetryOutcome
    {
        /// <summary>尝试成功（注册路据此留「已注册」取证行）。</summary>
        Succeeded,

        /// <summary>次数用尽而仍未就绪（调用点据此留「重试耗尽」行；<c>maxAttempts ≤ 0</c> 的配置形态也落此）。</summary>
        Exhausted,

        /// <summary>终止且不尝试补：退出期已销毁、其余异常（已由 <c>onGiveUp</c> 留痕）或取消——三者对调用点
        /// 不可区分，均不再记行（退出期常规路径，逐次留痕是噪音）。</summary>
        Stopped,
    }

    /// <summary>推送一行安装日志到引导页日志区（fire-and-forget，失败仅丢一行、不阻断主链路）。</summary>
    internal static void PushPreinstallLog(CurrentWindowAccessor accessor, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            _ = accessor.Current.EvaluateJavaScriptAsync(
                PreinstallEventScript(new PreinstallFrame("log", Line: line)));
        }
        catch (Exception)
        {
            // 页面未就绪/已导航：日志丢失可容忍（吞掉页面上游任何异常，仅丢一行日志）
        }
    }

    /// <summary>把自更新状态变化推给页面：插件监听 <c>dsh-desktop-update</c> CustomEvent 渲染更新按钮。
    /// 窗口未就绪即丢弃（不重试）——自更新状态机每次变化都会经 onTransition 再推，无需逐次送达。
    /// 保留 <see langword="static"/>，不降级为带重试的推送（自更新靠状态机后续变化补送）。</summary>
    internal static void PushUpdateState(CurrentWindowAccessor? accessor, UpdateState state)
    {
        try
        {
            // Current 在窗口未创建/已关闭时抛异常（非返回 null）：启动早期与退出阶段都会走到
            if (accessor?.Current is null)
            {
                return;
            }

            _ = accessor.Current.EvaluateJavaScriptAsync(
                "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-update',{detail:"
                + state.ToJson()
                + "}));}catch(e){}})();");
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未就绪：本次推送丢弃，后续状态变化会再推
        }
    }
}
