namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的网页会话自愈面（尺寸健康闸拆分，ADR webauth-token-reentry）：
/// 进入主界面后的鉴权页有界重进。决策在 <c>Core.WebAuthRecovery</c> 纯策略，此处仅编排
/// 探针/导航/日志（R1 组合根只装配）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>
    /// 网页会话落定自愈（ADR webauth-token-reentry）：第二跳提交后，token→303→cookie 链可能在
    /// WebView 未落定（终页为 dsh 401 文本）。探可见文本命中鉴权标记则有界重进 token URL 一次，
    /// 再坏只 fail loud（不挡启动、不循环）。探针超时/异常按未知跳过，绝不拖死启动。
    /// </summary>
    private async Task SettleWebSessionAsync(AppSetup app, DshWebUrl url, CancellationToken ct)
    {
        string? sample = await ProbeVisibleTextAsync(app, ct).ConfigureAwait(false);
        if (Core.WebAuthRecovery.Evaluate(sample, 0) != Core.WebAuthRecovery.Disposition.ReenterToken)
        {
            return;
        }

        HostLog.Write($"[nav] 检测到鉴权页，重进 token URL（第 1 次）");
        await NavigateAndAwaitCommitAsync(app, url.Value, ct);
        sample = await ProbeVisibleTextAsync(app, ct).ConfigureAwait(false);
        if (sample is null)
        {
            HostLog.Write("[nav] 重进后探针未知（超时/失败），无法确认自愈，按未知放行");
        }
        else if (Core.WebAuthRecovery.Evaluate(sample, 1) == Core.WebAuthRecovery.Disposition.GiveUp)
        {
            HostLog.Write("[nav] 鉴权页自愈失败（已重进）：页面仍要求认证，请重开 dsh 打印的 URL；启动继续");
        }
        else
        {
            HostLog.Write("[nav] 鉴权页自愈成功：重进后页面正常");
        }
    }

    /// <summary>探当前页可见文本采样（400 字截断）；有限重试后仍超时/异常返回 null（未知），
    /// 调用方按放行处理（ADR settle-gate-and-probe-retry：偶发 renderer 繁忙一次采样赌运气，
    /// 成功即返，耗尽才 Unknown；快机器零变化）。</summary>
    private async Task<string?> ProbeVisibleTextAsync(AppSetup app, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.AuthProbeTimeoutSeconds));
            try
            {
                // 经 AsTask 统一（ValueTask 无 WaitAsync；R1 S5 本地包装已删）。
                return await app.WindowAccessor.Current.EvaluateJavaScriptAsync(PageBridge.WebAuthProbe.Script).AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
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
