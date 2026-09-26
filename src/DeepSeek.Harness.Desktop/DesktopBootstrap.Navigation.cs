using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的导航调用面（尺寸健康闸拆分，ADR verdict-honesty-repair）：
/// Ryn 底层为同步原生 <c>set_url</c>（RynWebView.cs:413 实证），直接 <c>WaitAsync</c> 计时器挂不上——
/// <c>Task.Run</c> 先把同步段隔离进池线程再有界等。决策（超时值）在配置模型，此处仅编排调用/等待/日志
/// （R1 组合根只装配）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>有界导航调用：超时 loud 后返回（调用方沿"按已提交继续"走提交等待与探针）；
    /// 返回即记"调用已返回"；应用退出取消照常上抛。悬空池线程 fire-and-forget 可接受（探针先例）。</summary>
    /// <param name="accessor">当前窗口访问器。</param>
    /// <param name="target">导航靶点（日志仅记 authority，不记 query/token）。</param>
    /// <param name="timeoutSeconds">调用超时秒数（调用方传配置值）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private static async Task NavigateWithTimeoutAsync(
        CurrentWindowAccessor accessor, Uri target, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => accessor.Current.NavigateAsync(target).AsTask(), ct)
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);
            HostLog.Write($"[nav] 导航调用已返回：{target.GetLeftPart(UriPartial.Authority)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用退出：取消必须上抛（R2 B1），不吞。
            throw;
        }
        catch (TimeoutException)
        {
            HostLog.Write($"[nav] 导航调用超时（{timeoutSeconds}s），按已提交继续");
        }
    }
}
