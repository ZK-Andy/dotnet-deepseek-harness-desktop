using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>系统浏览器打开命令构造（纯函数 BuildProcessStartInfo）：Linux xdg-open 重定向形态与非 Linux UseShellExecute 形态。
/// 只测命令构造不真 spawn——真弹浏览器是环境副作用（xdg-open 依赖/CI 沙箱），Open 的 spawn 边界留给真机。</summary>
public class SystemBrowserOpenCommandTests
{
    /// <summary>验证 Linux 上构造 xdg-open 命令：FileName=xdg-open、参数为 URL、UseShellExecute=false 且重定向输出（浏览器后台报错不回灌应用终端）。</summary>
    [Fact]
    public void BuildProcessStartInfo_Linux_UsesXdgOpenWithRedirects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 分支按运行平台：非 Linux 无 xdg-open 形态
        }

        ProcessStartInfo psi = DeepSeek.Harness.Desktop.Services.SystemBrowser.BuildProcessStartInfo("https://example.com/x");

        Assert.Equal("xdg-open", psi.FileName);
        Assert.Contains("https://example.com/x", psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }

    /// <summary>验证非 Linux 平台构造 UseShellExecute=true 且 FileName=URL 的形态（OS 默认浏览器关联）。</summary>
    [Fact]
    public void BuildProcessStartInfo_NonLinux_UsesShellExecuteWithUrlAsFileName()
    {
        if (OperatingSystem.IsLinux())
        {
            return; // 本 CI/沙箱是 Linux：非 Linux 形态仅在 Windows/macOS 真机 CI 可断言
        }

        ProcessStartInfo psi = DeepSeek.Harness.Desktop.Services.SystemBrowser.BuildProcessStartInfo("https://example.com/x");

        Assert.Equal("https://example.com/x", psi.FileName);
        Assert.True(psi.UseShellExecute);
        Assert.False(psi.RedirectStandardOutput);
    }
}
