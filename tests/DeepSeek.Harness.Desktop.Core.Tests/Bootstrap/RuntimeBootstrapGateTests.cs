namespace DeepSeek.Harness.Desktop.Core.Tests.Bootstrap;

/// <summary>RuntimeBootstrapGate 记序语义（Signal/Reset 往返）——原与 shell 侧路由测试同文件，
/// 按「测谁归谁」拆归 Core.Tests（ADR post-packaging-churn-restructure 余批 G）。</summary>
public class RuntimeBootstrapGateTests
{
    /// <summary>验证 RuntimeBootstrapGate 记序往返：Signal 后 IsSignaled=true，Reset 后回到 false。</summary>
    [Fact]
    public void Gate_SignalAndReset_RoundTrip()
    {
        var gate = new RuntimeBootstrapGate();
        Assert.False(gate.IsSignaled);
        gate.Signal();
        Assert.True(gate.IsSignaled);
        gate.Reset();
        Assert.False(gate.IsSignaled);
    }
}
