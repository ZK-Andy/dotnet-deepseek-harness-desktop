using System.Text.Json;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 页面注入辅助（ADR 组合根只装配）：把 JS 注入类操作（横幅/引导进度/插件引导状态/日志回流/自更新状态）
/// 从组合根抽出为静态单点。均以 <see cref="CurrentWindowAccessor"/>
/// 为参数（未就绪的重试/丢弃语义在方法内），不依赖组合根实例状态。
/// </summary>
internal static class PagePump
{
    /// <summary>页面注入超时家（单例装载，见 <c>RuntimeTimeouts</c>）。</summary>
    private static readonly RuntimeTimeouts s_timeouts =
        RuntimeTimeouts.Load(AppContext.BaseDirectory);

    /// <summary>窗口就绪后注入横幅：Current 未就绪的 InvalidOperationException 按节拍重试（上限见配置）；
    /// 其余异常记日志放弃——横幅是增强告知，绝不拖垮启动链路。</summary>
    internal static async Task ShowBannerWhenReadyAsync(CurrentWindowAccessor accessor, string script, CancellationToken ct)
    {
        for (int attempt = 0; attempt < s_timeouts.BannerMaxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await accessor.Current.EvaluateJavaScriptAsync(script);
                return;
            }
            catch (InvalidOperationException)
            {
                // 窗口尚未创建/已销毁：稍后重试。一次性提示必须送达。
            }
            catch (Exception ex)
            {
                HostLog.Write($"[host] 横幅注入失败：{ex.Message}");
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(s_timeouts.BannerRetryDelaySeconds), ct);
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

    /// <summary>首跳 eval 导航脚本家（ADR smoke-witness-real-and-eval-first-hop）：页面内经
    /// <c>location.href</c> 发起顶层导航，绕过 saucer <c>set_url</c> 同步段在 arm64 runner 的 hang；
    /// 同步返回布尔供宿主判"已发出"（桥回报 JSON 形态经 <see cref="RynProbeValue"/> 解）。</summary>
    /// <param name="target">导航靶点。</param>
    /// <returns>同步返回布尔的 JS（<c>true</c> = 已发起）。</returns>
    internal static string EvalNavigateScript(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string urlJson = JsonSerializer.Serialize(target.ToString());
        return "(function(){try{location.href=" + urlJson + ";return true;}catch(e){return false;}})();";
    }

    /// <summary>经页面内 eval 发起导航（renderer 发起，arm64 原生 hang 绕行）。eval 缝以委托注入以便单测；
    /// 发不出/超时/异常一律 <c>false</c>（调用方回退原生导航），应用退出取消照常上抛（R2 B1）。
    /// 超时复用导航调用窗，不新增可调参数。</summary>
    /// <param name="evaluate">页面求值委托（宿主接 <c>CurrentWindowAccessor.Current.EvaluateJavaScriptAsync</c>）。</param>
    /// <param name="target">导航靶点。</param>
    /// <param name="timeoutSeconds">发出等待秒数（调用方传导航调用窗）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    /// <returns>true = eval 已发出且页内已发起导航；false = 未发出，调用方回退原生。</returns>
    internal static async Task<bool> TryNavigateViaEvalAsync(
        Func<string, CancellationToken, ValueTask<string>> evaluate, Uri target, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            string? raw = await evaluate(EvalNavigateScript(target), cts.Token).AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
            return string.Equals(RynProbeValue.Decode(raw), "true", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用退出：取消必须上抛（R2 B1），不吞。
            throw;
        }
        catch (Exception ex)
        {
            HostLog.Write($"[nav] 首跳 eval 导航未发出（回退原生）：{ex.Message}");
            return false;
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
