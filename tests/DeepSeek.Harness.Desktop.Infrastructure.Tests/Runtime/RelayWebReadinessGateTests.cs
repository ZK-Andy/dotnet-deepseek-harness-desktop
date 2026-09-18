using Probe = DeepSeek.Harness.Desktop.Infrastructure.Runtime.RuntimeLineageProbes.LoopbackWebProbe;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>
/// 接力就绪稳定窗（ADR relay-restart-client-module-collapse）：单拍 <c>Ready</c> 可能是将死前驱的应答，
/// 必须连续维持达稳定窗且期间无任何未就绪采样才算「已接稳」；任一非 Ready 采样清零重计。
/// </summary>
public class RelayWebReadinessGateTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>单拍 Ready 不接稳：首拍只是起算点。</summary>
    [Fact]
    public void SingleReadySample_IsNotStable()
    {
        var gate = new RelayWebReadinessGate(TimeSpan.FromSeconds(2));

        Assert.False(gate.Observe(Probe.Ready, s_t0));
    }

    /// <summary>Ready 连续维持满稳定窗即接稳（1s 节拍下第 3 拍命中）。</summary>
    [Fact]
    public void ReadyHeldForWindow_IsStable()
    {
        var gate = new RelayWebReadinessGate(TimeSpan.FromSeconds(2));

        Assert.False(gate.Observe(Probe.Ready, s_t0));
        Assert.False(gate.Observe(Probe.Ready, s_t0.AddSeconds(1)));
        Assert.True(gate.Observe(Probe.Ready, s_t0.AddSeconds(2)));
    }

    /// <summary>窗口内出现未就绪（端口无人 / web 面无声）即清零重计：把死前驱「应答一拍即断」的形态挡在收养之外。</summary>
    /// <param name="interruption">打断就绪的采样。</param>
    [Theory]
    [InlineData(Probe.NotServing)]
    [InlineData(Probe.ServingNotReady)]
    public void NonReadySample_ResetsWindow(Probe interruption)
    {
        var gate = new RelayWebReadinessGate(TimeSpan.FromSeconds(2));

        Assert.False(gate.Observe(Probe.Ready, s_t0));
        Assert.False(gate.Observe(interruption, s_t0.AddSeconds(2))); // 打断：原起算点作废
        Assert.False(gate.Observe(Probe.Ready, s_t0.AddSeconds(3)));  // 重新起算
        Assert.True(gate.Observe(Probe.Ready, s_t0.AddSeconds(5)));
    }

    /// <summary>非正稳定窗是配置错误：fail loud，不静默退化成「单拍即接稳」。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveWindow_Throws(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelayWebReadinessGate(TimeSpan.FromSeconds(seconds)));
    }
}
