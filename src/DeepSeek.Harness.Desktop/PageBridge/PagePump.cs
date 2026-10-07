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

    /// <summary>窗口就绪后注入横幅：Current 未就绪的 InvalidOperationException 按节拍重试（上限见配置）；
    /// 其余异常记日志放弃——横幅是增强告知，绝不拖垮启动链路。</summary>
    internal static Task ShowBannerWhenReadyAsync(CurrentWindowAccessor accessor, string script, CancellationToken ct) =>
        RetryInjectWhenReadyAsync(accessor, script, "banner", s_timeouts.BannerMaxAttempts,
            TimeSpan.FromSeconds(s_timeouts.BannerRetryDelaySeconds), ct);

    /// <summary>窗口就绪后注入自绘顶栏（ADR frameless-uniform-caption-bar）：重试语义与横幅同源
    /// （未就绪按节拍重试至上限，其余异常记日志放弃——顶栏属增强面，绝不拖垮启动/导航链路；
    /// 失败退路是托盘菜单的唤回/最大化/退出命令）。脚本幂等，导航后重注入无副作用。</summary>
    internal static Task InjectCaptionBarWhenReadyAsync(CurrentWindowAccessor accessor, string script, CancellationToken ct) =>
        RetryInjectWhenReadyAsync(accessor, script, "caption-bar", s_timeouts.PushMaxAttempts,
            TimeSpan.FromMilliseconds(s_timeouts.PushRetryDelayMilliseconds), ct);

    /// <summary>注册「每次页面加载执行」的顶栏脚本（ADR frameless-uniform-caption-bar「注入时机」条）：
    /// 三路注入里<b>唯一不靠导航事件覆盖后续每次加载</b>的一路——holder 页 <c>location.reload()</c> 进真 UI 是<b>同 URL
    /// 重载</b>，mac/win 的 <c>navigated</c> 回调在 URL/Source 未变时不发，真 UI 文档会因此拿不到顶栏
    /// （Linux 用户脚本逐文档重放，故该端导航钩子也覆盖）。webview 未就绪（<c>DeferredRynWebView</c> 在
    /// RunAsync 前抛 InvalidOperationException）按节拍重试至上限，其余异常留痕放弃——顶栏属增强面，
    /// 绝不拖垮启动链路。</summary>
    internal static Task RegisterCaptionBarScriptWhenReadyAsync(Func<IRynWebView> webView, string script, CancellationToken ct) =>
        RetryRegisterWhenReadyAsync(
            webView, script, s_timeouts.PushMaxAttempts, TimeSpan.FromMilliseconds(s_timeouts.PushRetryDelayMilliseconds), ct);

    /// <summary>同上，重试预算可注入（单测覆盖「预算耗尽」出口；生产走默认预算）。</summary>
    internal static Task RegisterCaptionBarScriptWhenReadyAsync(
        Func<IRynWebView> webView, string script, int maxAttempts, TimeSpan retryDelay, CancellationToken ct) =>
        RetryRegisterWhenReadyAsync(webView, script, maxAttempts, retryDelay, ct);

    /// <summary>脚本注册的有界重试单点：webview 未就绪按节拍重试，成功留一行取证日志（发版冒烟据此
    /// 核对「真 UI 文档拿到了顶栏」）；预算耗尽同样留痕（同文件另两个重试助手的口径）；退出期已销毁与
    /// 其余异常都放弃。</summary>
    private static async Task RetryRegisterWhenReadyAsync(
        Func<IRynWebView> webView, string script, int maxAttempts, TimeSpan retryDelay, CancellationToken ct)
    {
        for (int attempt = 0; attempt < maxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await webView().InjectScriptAsync(script, ct);
                HostLog.Write("[caption-bar] 每页加载脚本已注册（同 URL 重载不再依赖导航事件）");
                return;
            }
            catch (ObjectDisposedException)
            {
                // 退出期 webview 已销毁（本异常是 InvalidOperationException 的子类，须先于它捕获）：不再补注册
                return;
            }
            catch (InvalidOperationException)
            {
                // webview 尚未创建：稍后重试（DeferredRynWebView 的就绪契约）
            }
            catch (Exception ex)
            {
                HostLog.Write($"[caption-bar] 每页加载脚本注册失败（放弃）：{ex.Message}");
                return;
            }

            try
            {
                await Task.Delay(retryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!ct.IsCancellationRequested)
        {
            HostLog.Write($"[caption-bar] 每页加载脚本注册重试耗尽（{maxAttempts} 次后网页视图仍未就绪）");
        }
    }

    /// <summary>脚本注入的有界重试单点：Current 未就绪（InvalidOperationException）按节拍重试；
    /// 其余异常记日志放弃。重试上限/节拍由调用方给（横幅与顶栏各配各的）。</summary>
    private static async Task RetryInjectWhenReadyAsync(
        CurrentWindowAccessor accessor, string script, string scenario, int maxAttempts, TimeSpan retryDelay, CancellationToken ct)
    {
        for (int attempt = 0; attempt < maxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await accessor.Current.EvaluateJavaScriptAsync(script);
                return;
            }
            catch (InvalidOperationException)
            {
                // 窗口尚未创建/已销毁：稍后重试（banner 与 caption-bar 两场景共用本助手，预算见调用点）。
            }
            catch (Exception ex)
            {
                HostLog.Write($"[host] {scenario} 注入失败：{ex.Message}");
                return;
            }

            try
            {
                await Task.Delay(retryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>推一条引导进度到 wwwroot 引导页；未就绪按配置重试，耗尽记日志放弃。</summary>
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
        for (int attempt = 0; attempt < s_timeouts.PushMaxAttempts; attempt++)
        {
            try
            {
                await accessor.Current.EvaluateJavaScriptAsync(script);
                return;
            }
            catch (InvalidOperationException)
            {
                // 页面/窗口未就绪：稍后重试
            }
            catch (Exception ex)
            {
                HostLog.Write($"[bootstrap] 进度推送失败（放弃）：{ex.Message}");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(s_timeouts.PushRetryDelayMilliseconds));
        }

        HostLog.Write("[bootstrap] 进度推送重试耗尽（页面始终未就绪）");
    }

    /// <summary>构建 <c>dsh-desktop-preinstall</c> CustomEvent 注入脚本（detail 为帧对象 JSON）。</summary>
    internal static string PreinstallEventScript(PreinstallFrame frame)
    {
        string frameJson = JsonSerializer.Serialize(frame, AppJsonContext.Default.PreinstallFrame);
        return "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-preinstall',{detail:"
            + frameJson
            + "}));}catch(e){}})();";
    }

    /// <summary>推送一条插件引导状态（decision/installing/done）到引导页，带有限重试（同 PushBootstrapStateAsync）。</summary>
    internal static async Task RetryPushPreinstallAsync(CurrentWindowAccessor accessor, PreinstallFrame frame)
    {
        string script = PreinstallEventScript(frame);
        for (int attempt = 0; attempt < s_timeouts.PushMaxAttempts; attempt++)
        {
            try
            {
                await accessor.Current.EvaluateJavaScriptAsync(script);
                return;
            }
            catch (InvalidOperationException)
            {
                // 页面/窗口未就绪：稍后重试
            }
            catch (Exception ex)
            {
                HostLog.Write($"[preinstall] 状态推送失败（放弃）：{ex.Message}");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(s_timeouts.PushRetryDelayMilliseconds));
        }

        HostLog.Write("[preinstall] 状态推送重试耗尽（页面始终未就绪）");
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
