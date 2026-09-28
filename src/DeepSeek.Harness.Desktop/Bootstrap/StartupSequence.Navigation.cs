using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// <see cref="StartupSequence"/> 的导航调用与窗口就绪等待面（原组合根 Navigation/WindowReady 分部随编排搬迁，
/// ADR compose-root-form-separation）：Ryn 底层为同步原生 <c>set_url</c>（RynWebView.cs:413 实证），直接
/// <c>WaitAsync</c> 计时器挂不上——<c>Task.Run</c> 先把同步段隔离进池线程再有界等。决策（超时值）在配置模型，
/// 此处仅编排调用/等待/日志（R1）。
/// </summary>
internal sealed partial class StartupSequence
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

    /// <summary>导航并等待其真正提交（<see cref="RynNavigationCallbacks"/> 的
    /// 「导航已到达」信号，先订阅后导航避免错过）。提交信号用于隔开两跳导航——
    /// <c>NavigateAsync</c> 连发会被 WebKitGTK 合并，前一跳尚未发出即被后一跳覆盖。
    /// 等待超时按「已提交」降级继续（信号只是隔跳手段，缺位时不比单跳直导更差）；
    /// 调用本身亦有界（ADR navigate-call-timeout：arm64 实证原生调用可挂起，无界等即永卡）；
    /// 取消（应用退出）照常传播。</summary>
    /// <param name="target">导航靶点。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task NavigateAndAwaitCommitAsync(Uri target, CancellationToken ct)
    {
        RynNavigationCallbacks callbacks =
            _app.App.Services.GetRequiredService<RynNavigationCallbacks>();
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        callbacks.SetOnNavigated(() => arrived.TrySetResult());
        try
        {
            HostLog.Write($"[nav] 发起导航：{target.GetLeftPart(UriPartial.Authority)}");
            await NavigateWithTimeoutAsync(_app.WindowAccessor, target, _timeouts.NavCallTimeoutSeconds, ct).ConfigureAwait(false);
            await WaitNavCommitAsync(arrived.Task, "导航", ct).ConfigureAwait(false);
        }
        finally
        {
            callbacks.SetOnNavigated(static () => { });
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
            await arrivedTask.WaitAsync(TimeSpan.FromSeconds(_timeouts.NavCommitTimeoutSeconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            HostLog.Write($"[nav] {hop}提交等待超时（{_timeouts.NavCommitTimeoutSeconds}s），按已提交继续");
        }
    }

    /// <summary>等主窗口可用：1s 轮询有界等；应用退出取消照常上抛，超时回 false（调用方 loud 跳过导航）。
    /// Ryn 原生建窗可能慢于 dsh 就位（CI 无 D-Bus 会话实证 30s+），首个 <c>Current</c> 即抛不再直接失败收口
    /// （ADR bootstrap-window-ready-wait）；决策（超时值）在配置模型 <c>RuntimeTimeouts.WindowReadyTimeoutSeconds</c>。</summary>
    /// <param name="ct">引导任务取消令牌。</param>
    /// <returns>窗口就绪 true；超时 false。</returns>
    private async Task<bool> WaitForWindowAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeouts.WindowReadyTimeoutSeconds));
        while (true)
        {
            try
            {
                _ = _app.WindowAccessor.Current;
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
