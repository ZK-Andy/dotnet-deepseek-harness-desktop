using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>系统浏览器打开命令构造（纯函数 BuildProcessStartInfo + PATH 探测 FindOnPath）：
/// Linux 直启/scope 两形态、非 Linux UseShellExecute 形态、launcher 探测命中与未命中。
/// 只测命令构造不真 spawn——真弹浏览器是环境副作用（xdg-open 依赖/CI 沙箱），Open 的 spawn 边界留给真机。</summary>
public class SystemBrowserOpenCommandTests
{
    /// <summary>验证 Linux 无 launcher 时构造 xdg-open 命令：FileName=xdg-open、参数为 URL、
    /// UseShellExecute=false 且重定向输出（浏览器后台报错不回灌应用终端）。</summary>
    [Fact]
    public void BuildProcessStartInfo_Linux_WithoutScopeLauncher_UsesXdgOpenWithRedirects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 分支按运行平台：非 Linux 无 xdg-open 形态
        }

        ProcessStartInfo psi = SystemBrowser.BuildProcessStartInfo("https://example.com/x", scopeLauncher: null);

        Assert.Equal("xdg-open", psi.FileName);
        Assert.Contains("https://example.com/x", psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }

    /// <summary>验证 Linux 有 launcher 时把 xdg-open 包进独立 transient scope（ADR exit-app-scope-ghost-residue）：
    /// FileName=launcher、参数为 --user/--scope/--collect/--quiet/-- 前缀 + xdg-open + URL，仍重定向输出。</summary>
    [Fact]
    public void BuildProcessStartInfo_Linux_WithScopeLauncher_WrapsInTransientScope()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // scope 形态是 Linux/systemd 专属
        }

        ProcessStartInfo psi = SystemBrowser.BuildProcessStartInfo(
            "https://example.com/x", scopeLauncher: "/usr/bin/systemd-run");

        Assert.Equal("/usr/bin/systemd-run", psi.FileName);
        Assert.Equal(
            ["--user", "--scope", "--collect", "--quiet", "--", "xdg-open", "https://example.com/x"],
            psi.ArgumentList);
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

        ProcessStartInfo psi = SystemBrowser.BuildProcessStartInfo("https://example.com/x", scopeLauncher: null);

        Assert.Equal("https://example.com/x", psi.FileName);
        Assert.True(psi.UseShellExecute);
        Assert.False(psi.RedirectStandardOutput);
    }

    /// <summary>PATH 探测：按段序取首个命中；空段跳过；null PATH 与全未命中返回 null。</summary>
    [Fact]
    public void FindOnPath_TakesFirstHit_SkipsEmptySegments_ElseNull()
    {
        char sep = Path.PathSeparator;
        string probe = Path.Combine("/opt/bin", "systemd-run");

        Assert.Equal(probe, SystemBrowser.FindOnPath(
            "systemd-run", $"/usr/lib{sep}{sep}  /opt/bin  ", p => p == probe));
        Assert.Null(SystemBrowser.FindOnPath("systemd-run", "/usr/lib", _ => false));
        Assert.Null(SystemBrowser.FindOnPath("systemd-run", pathEnv: null, _ => true));
    }
}
