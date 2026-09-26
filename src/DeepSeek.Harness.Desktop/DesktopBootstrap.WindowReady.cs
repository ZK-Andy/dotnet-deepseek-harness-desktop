namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的窗口就绪等待面（尺寸健康闸拆分，ADR bootstrap-window-ready-wait）：
/// Ryn 原生建窗可能慢于 dsh 就位（CI 无 D-Bus 会话实证 30s+），首个 <c>Current</c> 即抛不再直接失败收口。
/// 决策（超时值）在配置模型 <c>RuntimeTimeouts.WindowReadyTimeoutSeconds</c>，此处仅编排轮询/取消/日志
/// （R1 组合根只装配）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>等主窗口可用：1s 轮询有界等；应用退出取消照常上抛，超时回 false（调用方 loud 跳过导航）。</summary>
    /// <param name="app">Ryn 应用装配产出（窗口访问器来源）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    /// <returns>窗口就绪 true；超时 false。</returns>
    private async Task<bool> WaitForWindowAsync(AppSetup app, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.WindowReadyTimeoutSeconds));
        while (true)
        {
            try
            {
                _ = app.WindowAccessor.Current;
                return true;
            }
            catch (InvalidOperationException)
            {
                // 窗口尚未建好（Ryn"无窗口/未运行"同此型）：继续等；其他异常照抛 fail loud。
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_timeouts.WindowReadyPollIntervalSeconds), cts.Token).ConfigureAwait(false);
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
}
