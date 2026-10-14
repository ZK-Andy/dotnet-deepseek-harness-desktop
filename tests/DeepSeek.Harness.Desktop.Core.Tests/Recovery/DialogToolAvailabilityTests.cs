using DeepSeek.Harness.Desktop.Core.Recovery;

namespace DeepSeek.Harness.Desktop.Core.Tests.Recovery;

/// <summary>原生对话框可用性判定（ADR native-failure-surface）：Windows/macOS 恒可用；Linux 需 zenity/kdialog 之一在 PATH 上；其余平台不可用。</summary>
public class DialogToolAvailabilityTests
{
    private static readonly string s_paths = string.Join(Path.PathSeparator, "/usr/bin", "/usr/local/bin");

    /// <summary>可用：Windows/macOS 系统通道不探 PATH；Linux 命中候选即用，探测按候选序（zenity 先于 kdialog）。</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void IsAvailable_ChannelPresent_True(bool isWindows, bool isMacOs, bool isLinux)
    {
        var probed = new List<string>();
        Assert.True(DialogToolAvailability.IsAvailable(isWindows, isMacOs, isLinux, s_paths, p =>
        {
            probed.Add(Path.GetFileName(p));
            return true;
        }));
        if (isLinux)
        {
            Assert.Equal("zenity", probed[0]);
        }
    }

    /// <summary>无可用通道（Linux 无工具 / 非三大平台）即不可用——插件此刻静默不弹，必须回落。</summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void IsAvailable_NoChannel_False(bool isWindows, bool isMacOs, bool isLinux)
    {
        Assert.False(DialogToolAvailability.IsAvailable(isWindows, isMacOs, isLinux, s_paths, _ => false));
    }

    /// <summary>PATH 为空/未设：不可用且不抛（探测的 IsNullOrEmpty 早退）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsAvailable_EmptyPath_False(string? pathVariable)
    {
        Assert.False(DialogToolAvailability.IsAvailable(false, false, true, pathVariable, _ => true));
    }
}
