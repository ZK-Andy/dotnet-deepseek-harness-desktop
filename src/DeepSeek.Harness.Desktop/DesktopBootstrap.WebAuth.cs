namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的网页会话自愈面（尺寸健康闸拆分，ADR webauth-token-reentry）：
/// 进入主界面后的鉴权页有界重进 + 终页裁决。决策在 <c>Core.WebAuthRecovery</c> 纯策略，
/// 此处仅编排探针/导航/日志（R1 组合根只装配）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>
    /// 网页会话落定自愈（ADR webauth-token-reentry + page-verdict-gate）：第二跳提交后，
    /// token→303→cookie 链可能在 WebView 未落定（终页为 dsh 401 文本）。裁决为鉴权页则有界重进
    /// token URL 一次，再坏只 fail loud（不挡启动、不循环）。探针超时/异常按未知放过启动；
    /// 冒烟显示腿（deb）只认 <c>页面裁决=healthy</c>（unknown/auth 皆红），未置位腿（mac/win）仍按到达落定、auth 仍红。
    /// </summary>
    private async Task SettleWebSessionAsync(AppSetup app, DshWebUrl url, CancellationToken ct)
    {
        string expectedOrigin = url.Authority;
        string? sample = await ProbePageSampleAsync(app, ct).ConfigureAwait(false);
        Core.WebAuthRecovery.PageVerdictDetail detail = Core.WebAuthRecovery.ClassifyDetail(sample, expectedOrigin);
        if (detail.Verdict != Core.WebAuthRecovery.PageVerdict.Auth)
        {
            LogPageVerdict(detail, expectedOrigin, reentered: false);
            return;
        }

        HostLog.Write("[nav] 检测到鉴权页，重进 token URL（第 1 次）");
        await NavigateAndAwaitCommitAsync(app, url.Value, ct);
        sample = await ProbePageSampleAsync(app, ct).ConfigureAwait(false);
        LogPageVerdict(Core.WebAuthRecovery.ClassifyDetail(sample, expectedOrigin), expectedOrigin, reentered: true);
    }

    /// <summary>
    /// 终页裁决的唯一留痕点（ADR page-verdict-gate）：只记实际 origin 与文本长度，不记内容
    /// （防会话文本落盘；origin 不含 token 查询串）。冒烟门禁 grep 本行的
    /// <c>healthy</c>／<c>auth</c>／<c>unknown</c> 三态——一行一事实，绿即"同源且非鉴权页"。
    /// </summary>
    /// <param name="detail">Core 裁决明细（采样只拆一次，此处不重复拆）。</param>
    /// <param name="expectedOrigin">期望 origin（无采样时用于留痕）。</param>
    /// <param name="reentered">是否已重进过 token URL。</param>
    private static void LogPageVerdict(Core.WebAuthRecovery.PageVerdictDetail detail, string expectedOrigin, bool reentered)
    {
        string tail = reentered ? "，重进后" : string.Empty;
        string where = detail.HasSample
            ? $"origin={detail.Origin} 可见文本 {detail.VisibleTextLength} 字"
            : $"探针无采样，期望 origin={expectedOrigin}";
        switch (detail.Verdict)
        {
            case Core.WebAuthRecovery.PageVerdict.Healthy:
                HostLog.Write($"[nav] 页面裁决={Core.WebAuthRecovery.VerdictHealthy}（{where}{tail}）");
                break;
            case Core.WebAuthRecovery.PageVerdict.Auth:
                HostLog.Write($"[nav] 页面裁决={Core.WebAuthRecovery.VerdictAuth}（{where}{tail}，请重开 dsh 打印的 URL；启动继续）");
                break;
            default:
                HostLog.Write($"[nav] 页面裁决={Core.WebAuthRecovery.VerdictUnknown}（{where}{tail}）");
                break;
        }
    }

    /// <summary>探当前页采样（origin + 400 字可见文本，<see cref="Core.PageProbeSample"/> 形态）；
    /// 有限重试后仍超时/异常返回 null（未知），调用方按放过启动处理（ADR settle-gate-and-probe-retry：
    /// 偶发 renderer 繁忙一次采样赌运气，成功即返，耗尽才 Unknown；快机器零变化）。</summary>
    private async Task<string?> ProbePageSampleAsync(AppSetup app, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.AuthProbeTimeoutSeconds));
            try
            {
                // 经 AsTask 统一（ValueTask 无 WaitAsync；R1 S5 本地包装已删）。
                // 桥回报是 JSON 文档：字符串带引号与转义，先经 RynProbeValue 解码再交裁决（否则 origin 带引号，同源恒不成立）。
                string? raw = await app.WindowAccessor.Current.EvaluateJavaScriptAsync(PageBridge.WebAuthProbe.Script).AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
                return PageBridge.RynProbeValue.Decode(raw);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 应用退出：取消必须上抛（R2 B1），不吞。
                throw;
            }
            catch (OperationCanceledException) when (attempt >= _timeouts.AuthProbeAttempts)
            {
                HostLog.Write($"[nav] 鉴权探针超时（{_timeouts.AuthProbeTimeoutSeconds}s×{attempt}次），跳过自愈检查");
                return null;
            }
            catch (OperationCanceledException)
            {
                HostLog.Write($"[nav] 鉴权探针超时（{_timeouts.AuthProbeTimeoutSeconds}s），第{attempt + 1}次重试");
            }
            catch (Exception ex) when (attempt >= _timeouts.AuthProbeAttempts)
            {
                HostLog.Write($"[nav] 鉴权探针失败（跳过自愈检查）：{ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                HostLog.Write($"[nav] 鉴权探针失败，第{attempt + 1}次重试：{ex.Message}");
            }
        }
    }
}
