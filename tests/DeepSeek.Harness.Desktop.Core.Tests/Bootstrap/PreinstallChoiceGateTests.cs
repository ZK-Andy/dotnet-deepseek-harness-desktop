namespace DeepSeek.Harness.Desktop.Core.Tests.Bootstrap;

/// <summary>PreinstallChoiceGate 决策语义（一次拍板锁定、Reset 可重走）——原与 shell 侧路由/帧测试
/// 同文件，按「测谁归谁」拆归 Core.Tests（ADR post-packaging-churn-restructure 余批 G）。</summary>
public class PreinstallChoiceGateTests
{
    /// <summary>验证 PreinstallChoiceGate 初始未决策（IsDecided=false）：首个 Set 之前闸门须处于未拍板态。</summary>
    [Fact]
    public void Gate_NotDecided_Initially()
    {
        var gate = new PreinstallChoiceGate();
        Assert.False(gate.IsDecided);
    }

    /// <summary>验证 Set(Install) 使闸门转为已决策，且等待中的 Choice 返回 Install。</summary>
    [Fact]
    public async Task Gate_SetInstall_CompletesChoiceWithInstall()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        Assert.True(gate.IsDecided);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    /// <summary>验证 Set(Skip) 使闸门转为已决策，且等待中的 Choice 返回 Skip。</summary>
    [Fact]
    public async Task Gate_SetSkip_CompletesChoiceWithSkip()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Skip);
        Assert.True(gate.IsDecided);
        Assert.Equal(PreinstallChoice.Skip, await gate.Choice);
    }

    /// <summary>验证一次拍板即锁定：先 Install 再 Set(Skip)，Choice 仍为 Install，第二次 Set 被忽略。</summary>
    [Fact]
    public async Task Gate_SecondSet_Ignored()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        gate.Set(PreinstallChoice.Skip);
        Assert.Equal(PreinstallChoice.Install, await gate.Choice);
    }

    /// <summary>验证 Reset 清除已拍板决策，闸门回到未决策态，可重新走选择流程。</summary>
    [Fact]
    public void Gate_Reset_ClearsDecision()
    {
        var gate = new PreinstallChoiceGate();
        gate.Set(PreinstallChoice.Install);
        gate.Reset();
        Assert.False(gate.IsDecided);
    }
}
