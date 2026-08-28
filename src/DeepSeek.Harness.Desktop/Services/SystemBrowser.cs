using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 系统默认浏览器打开（纯静态，可共享）：桌面壳把 WebView 内的站外 http(s) URL
/// 交给系统默认浏览器，而不是让它把页面导航走。Linux 走 <c>xdg-open</c>（重定向
/// 子进程输出，浏览器后台报错不回灌应用终端），其余平台 <c>Process.Start(UseShellExecute=true)</c>。
/// </summary>
/// <remarks>
/// 单一事实源：<see cref="ExternalLinkCommandRouter"/> 与 <see cref="RynNavigationCallbacks"/>
/// 的默认打开器都收敛到这里，避免两份复制打开逻辑漂移。
/// </remarks>
public static class SystemBrowser
{
    /// <summary>用系统默认浏览器打开 <paramref name="url"/>。返回是否已启动。</summary>
    public static bool Open(string url)
    {
        var psi = new ProcessStartInfo();
        if (OperatingSystem.IsLinux())
        {
            psi.FileName = "xdg-open";
            psi.ArgumentList.Add(url);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        else
        {
            psi.FileName = url;
            psi.UseShellExecute = true;
        }

        using var p = Process.Start(psi);
        if (p is null)
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            // 必须持续排空重定向缓冲区，否则子进程写满管道会阻塞；内容刻意丢弃
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        return !p.HasExited;
    }
}
