using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 平台安装执行器：派生「等待本进程退出 → 静默安装 → 拉起新版」的分离进程。
/// Linux 用 pkexec（弹一次系统授权框），并**等待授权结果**——用户取消授权时抛出，
/// 状态机据此回退 ready；授权通过后脚本接管（本进程即将退出，等待自然超时返回）。
/// Windows 走 Inno Setup 静默参数；macOS v1 不支持静默，转 Error。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>pkexec 授权窗口的最长观察时间：超时视为授权通过、安装进行中。</summary>
    private static readonly TimeSpan LaunchObserveWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 启动安装流程。Linux 下等待授权窗口：快速非零退出（用户取消/拒绝）抛
    /// <see cref="InvalidOperationException"/>；仍在运行则正常返回（安装已展开）。
    /// </summary>
    /// <param name="assetPath">已校验的安装包本地路径。</param>
    /// <param name="workDir">脚本等辅助文件的落盘目录（updates 目录）。</param>
    public static async Task LaunchAsync(string assetPath, string workDir, CancellationToken cancellationToken)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前可执行文件路径");
        if (OperatingSystem.IsLinux())
        {
            using var p = LaunchLinux(assetPath, workDir, exePath);
            using var observe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            observe.CancelAfter(LaunchObserveWindow);
            try
            {
                await p.WaitForExitAsync(observe.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 观察窗口内未退出 = 授权通过、脚本在等本进程退出：安装已展开
                return;
            }

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"授权被取消或失败（pkexec exit {p.ExitCode}）");
            }

            return;
        }

        if (OperatingSystem.IsWindows())
        {
            LaunchWindows(assetPath);
            return;
        }

        throw new PlatformNotSupportedException("macOS 静默更新暂未支持；请手动下载 dmg 安装");
    }

    private static Process LaunchLinux(string assetPath, string workDir, string exePath)
    {
        var ext = Path.GetExtension(assetPath).ToLowerInvariant();
        var installCmd = ext switch
        {
            ".deb" => $"dpkg -i '{EscapeSingle(assetPath)}'",
            ".rpm" => $"rpm -U --replacepkgs --quiet '{EscapeSingle(assetPath)}'",
            _ => throw new PlatformNotSupportedException($"Linux 不支持的安装包类型：{ext}"),
        };

        // 等本进程退出再装（文件占用），装完拉起新版同路径二进制。
        var script = $"""
            #!/bin/sh
            while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.3; done
            {installCmd} || exit 1
            nohup '{EscapeSingle(exePath)}' >/dev/null 2>&1 &
            """;
        Directory.CreateDirectory(workDir);
        var scriptPath = Path.Combine(workDir, "install.sh");
        File.WriteAllText(scriptPath, script + Environment.NewLine);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "pkexec",
            ArgumentList = { "/bin/sh", scriptPath },
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("pkexec 启动失败");
        return p;
    }

    private static void LaunchWindows(string assetPath)
    {
        // Inno Setup：/SILENT 静默、/CLOSEAPPLICATIONS 等文件解锁、/RESTARTAPPLICATIONS 装完自动拉起
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = assetPath,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("安装器启动失败");
        _ = p;
    }

    private static string EscapeSingle(string s) => s.Replace("'", "'\\''", StringComparison.Ordinal);
}
