namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Bootstrap;

/// <summary>插件引导无人值守跳过（P2）：仅显式 opt-in 跳过，未设置/未知值一律走用户决策。真纯函数，无副作用。</summary>
public class FirstBootPreinstallSkipTests
{
    /// <summary>opt-in 值判定矩阵（大小写不敏感；fail-closed）。</summary>
    [Theory]
    [InlineData("skip", true)]
    [InlineData("SKIP", true)]
    [InlineData("Skip", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData("1", false)]
    public void ShouldAutoSkipPreinstall_Matrix(string? value, bool expected)
    {
        Assert.Equal(expected, FirstBootBootstrapService.ShouldAutoSkipPreinstall(value));
    }
}
