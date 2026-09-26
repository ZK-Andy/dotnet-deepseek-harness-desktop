namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>鉴权自愈裁决（ADR webauth-token-reentry）：命中标记 + 预算未用完才重进；未知放行，耗尽放弃。</summary>
public class WebAuthRecoveryTests
{
    /// <summary>探针超时/失败（null）→ 放行：绝不因观测失败触发重进。</summary>
    [Fact]
    public void NullText_IsHealthy()
    {
        Assert.Equal(WebAuthRecovery.Disposition.Healthy, WebAuthRecovery.Evaluate(null, 0));
    }

    /// <summary>空文本/正常页面文本 → 放行。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DeepSeek Harness")]
    public void NonAuthText_IsHealthy(string text)
    {
        Assert.Equal(WebAuthRecovery.Disposition.Healthy, WebAuthRecovery.Evaluate(text, 0));
    }

    /// <summary>鉴权标记 + 未重进过 → 重进（含前后缀包裹形态）。</summary>
    [Theory]
    [InlineData("dsh web authentication required; reopen the URL printed by dsh web.")]
    [InlineData("xx authentication required yy")]
    public void AuthText_FirstTime_Reenters(string text)
    {
        Assert.Equal(WebAuthRecovery.Disposition.ReenterToken, WebAuthRecovery.Evaluate(text, 0));
    }

    /// <summary>鉴权标记 + 已重进过（达上限）→ 放弃，不循环。</summary>
    [Fact]
    public void AuthText_BudgetSpent_GivesUp()
    {
        const string auth = "dsh web authentication required; reopen the URL printed by dsh web.";

        Assert.Equal(WebAuthRecovery.Disposition.GiveUp, WebAuthRecovery.Evaluate(auth, 1));
        Assert.Equal(WebAuthRecovery.Disposition.GiveUp, WebAuthRecovery.Evaluate(auth, 5));
    }

    /// <summary>大小写敏感：上游固定小写串，大写变体不误伤。</summary>
    [Fact]
    public void WrongCaseMarker_IsHealthy()
    {
        Assert.Equal(WebAuthRecovery.Disposition.Healthy, WebAuthRecovery.Evaluate("Authentication Required", 0));
    }
}
