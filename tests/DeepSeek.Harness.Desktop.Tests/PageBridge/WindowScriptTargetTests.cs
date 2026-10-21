namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>窗口脚本求值端口的 Ryn 适配器（ADR architecture/2026-10-21-window-script-target-port）：
/// 提供者缺位（Build 之前尚无窗口访问器）归一化为「未就绪」——与重试单点的判据同型，故抛
/// <see cref="InvalidOperationException"/> 而非别的异常（其余异常一律照抛，交调用方 fail loud）。</summary>
public class WindowScriptTargetTests
{
    /// <summary>访问器提供者尚未有值：求值抛「未就绪」型异常（不是 NRE，也不是别的异常型）。</summary>
    [Fact]
    public void EvaluateAsync_ProviderMissing_ThrowsNotReady()
    {
        var target = new WindowScriptTarget(() => null);

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = target.EvaluateAsync("SCRIPT", CancellationToken.None);
        });
    }
}
