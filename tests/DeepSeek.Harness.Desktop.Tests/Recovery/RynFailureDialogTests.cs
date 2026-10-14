namespace DeepSeek.Harness.Desktop.Tests.Recovery;

/// <summary>失败面原生对话框适配器（ADR native-failure-surface）：确认通道经注入缝取证（同步模态封送池线程），可用性按平台与 PATH 判定。</summary>
public class RynFailureDialogTests
{
    /// <summary>确认通道把标题/正文原样交给原生通道，并回传其结论（true=继续自动恢复）。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmAsync_DelegatesToNativeChannel(bool keepRecovering)
    {
        var seen = new List<string>();
        var dialog = new RynFailureDialog(confirm: (title, message) =>
        {
            seen.Add($"{title}|{message}");
            return keepRecovering;
        });

        Assert.Equal(keepRecovering, await dialog.ConfirmAsync("标题", "正文", CancellationToken.None));
        Assert.Equal(new[] { "标题|正文" }, seen);
    }

    /// <summary>可用性按平台与 PATH 判定：命中工具即可用；Linux 无工具即不可用（必须回落，不得静默消失）。</summary>
    [Fact]
    public void IsAvailable_FollowsPlatformAndPath()
    {
        Assert.True(new RynFailureDialog(isExecutable: _ => true).IsAvailable);
        if (OperatingSystem.IsLinux())
        {
            Assert.False(new RynFailureDialog(isExecutable: _ => false).IsAvailable);
        }
    }
}
