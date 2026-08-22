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

    /// <summary>按安装包扩展名生成包管理器命令（纯函数，可单测）。</summary>
    public static string InstallCommandFor(string assetPath)
    {
        var ext = Path.GetExtension(assetPath).ToLowerInvariant();
        return ext switch
        {
            ".deb" => $"dpkg -i '{EscapeSingle(assetPath)}'",
            ".rpm" => $"rpm -U --replacepkgs --quiet '{EscapeSingle(assetPath)}'",
            _ => throw new PlatformNotSupportedException($"Linux 不支持的安装包类型：{ext}"),
        };
    }

    /// <summary>
    /// 生成 Linux 安装脚本内容（纯字符串，可单测）：等本进程退出 → 装包 → 把新版**降权回原用户**
    /// 拉起（runuser + PKEXEC_UID），输出追 install.log。GUI 会话与隔离变量在脚本内以 <c>$VAR</c> 引用，
    /// 由 pkexec env 注入；此处只固定 DSH_HOME 覆盖与二进制路径。
    /// </summary>
    public static string BuildLinuxScript(string installCommand, string logPath, int processId, string exePath, string? dshHomeOverride)
    {
        return $"""
            #!/bin/sh
            exec >> '{EscapeSingle(logPath)}' 2>&1
            echo "== install start $(date) pid={processId}"
            echo "DISPLAY=$DISPLAY WAYLAND_DISPLAY=$WAYLAND_DISPLAY"
            while kill -0 {processId} 2>/dev/null; do sleep 0.3; done
            echo "app exited; running: {installCommand}"
            {installCommand}
            echo "install exit=$?"
            if [ -n "$PKEXEC_UID" ]; then
              REL_USER="$(getent passwd "$PKEXEC_UID" 2>/dev/null | cut -d: -f1)"
              echo "relaunch as uid=$PKEXEC_UID user=$REL_USER"
              runuser -u "$REL_USER" -- env DISPLAY="$DISPLAY" WAYLAND_DISPLAY="$WAYLAND_DISPLAY" \
                XAUTHORITY="$XAUTHORITY" XDG_RUNTIME_DIR="$XDG_RUNTIME_DIR" \
                DBUS_SESSION_BUS_ADDRESS="$DBUS_SESSION_BUS_ADDRESS" \
                PATH="$PATH" HOME="$HOME" DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_X64="$DOTNET_ROOT_X64" \
                DSH_DESKTOP_DSH_HOME="{EscapeSingle(dshHomeOverride ?? string.Empty)}" \
                nohup '{EscapeSingle(exePath)}' >> '{EscapeSingle(logPath)}' 2>&1 &
            else
              echo "relaunch as current user"
              nohup '{EscapeSingle(exePath)}' >> '{EscapeSingle(logPath)}' 2>&1 &
            fi
            """;
    }

    private static Process LaunchLinux(string assetPath, string workDir, string exePath)
    {
        var installCmd = InstallCommandFor(assetPath);
        var logPath = Path.Combine(workDir, "install.log");
        var script = BuildLinuxScript(
            installCmd,
            logPath,
            Environment.ProcessId,
            exePath,
            Environment.GetEnvironmentVariable(DevEnvironment.HomeOverrideEnv));
        Directory.CreateDirectory(workDir);
        var scriptPath = Path.Combine(workDir, "install.sh");
        File.WriteAllText(scriptPath, script + Environment.NewLine);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // pkexec 会重置环境：显式透传 GUI 会话变量（否则拉起的新版窗口起不来）、
        // 开发隔离变量（否则重启后的实例丢掉 DSH_HOME 隔离）与 .NET 运行时定位
        // （DOTNET_ROOT 缺失时 apphost 报 ".NET location: Not found"——实机教训）
        var psi = new ProcessStartInfo
        {
            FileName = "pkexec",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("env");
        var passthrough = new[]
        {
            "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS",
            DevEnvironment.HomeOverrideEnv,
            "PATH", "HOME", "DOTNET_ROOT", "DOTNET_ROOT_X64",
        };
        foreach (var key in passthrough)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                psi.ArgumentList.Add($"{key}={value}");
            }
        }

        psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add(scriptPath);
        return Process.Start(psi) ?? throw new InvalidOperationException("pkexec 启动失败");
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
