namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 引导落定握手原语（ADR composition-root-value-flow-pipeline 批次 0 记序安全网，批次 1 随引导服务下沉）：
/// 监督器进入监视与共享 home 横幅共用「等引导终态句柄置位（可带超时降级）」的单一等待点——
/// 引导未落定前监督器不得空转进恢复循环（恢复屏会覆写引导页）、横幅不得抢跑版本探测。
/// </summary>
public static class BootstrapSettleGate
{
    /// <summary>等待引导落定。<paramref name="settled"/> 为 null（本轮无首启引导）立即放行；
    /// 置位或超时放行（超时 = 调用方「迟迟未定按已定继续」的降级语义）；取消返回 false。</summary>
    /// <param name="settled">引导终态句柄；null = 本轮无首启引导。</param>
    /// <param name="timeout">降级时限；null = 无限等（监督器语义：落定前绝不进入监视）。</param>
    /// <param name="ct">应用退出取消令牌。</param>
    /// <returns>true = 可继续；false = 已取消，调用方应立即返回。</returns>
    public static async Task<bool> WaitSettledAsync(
        TaskCompletionSource? settled, TimeSpan? timeout, CancellationToken ct)
    {
        if (settled is null)
        {
            return true;
        }

        try
        {
            // WaitAsync(InfiniteTimeSpan, ct) = 无限等（带取消），与 timeout:null 同义单点化。
            await settled.Task.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            // 引导链路迟迟未定也照常继续：等待方是增强信息，不能因超时路径永久缺席
        }

        return true;
    }
}
