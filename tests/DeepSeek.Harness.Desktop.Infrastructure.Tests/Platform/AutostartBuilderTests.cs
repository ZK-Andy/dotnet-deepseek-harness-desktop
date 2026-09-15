
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>开机自启条目文本构造器（纯函数）。</summary>
public class AutostartBuilderTests
{
    /// <summary>验证 Linux 开机自启 desktop 条目含 Exec 指向与 X-GNOME-Autostart-enabled 标记。</summary>
    [Fact]
    public void BuildLinuxDesktopEntry_ContainsExecAndAutostartFlag()
    {
        string entry = Autostart.BuildLinuxDesktopEntry("/opt/app/bin/desktop");
        Assert.Contains("Exec=/opt/app/bin/desktop", entry);
        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
    }

    /// <summary>验证 macOS 开机自启 plist 钉住 Label、参数与 RunAtLoad 标记。</summary>
    [Fact]
    public void BuildMacOSPlist_PinsLabelArgumentsAndRunAtLoad()
    {
        string plist = Autostart.BuildMacOSPlist("/Applications/App.app/Contents/MacOS/exec");
        Assert.Contains("<key>Label</key>", plist);
        Assert.Contains("io.github.zk-andy.dotnet-deepseek-harness-desktop", plist);
        Assert.Contains("/Applications/App.app/Contents/MacOS/exec", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("<true/>", plist);
    }
}
