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

        // 等本进程退出再装（文件占用），装完把新版**降权回原用户**拉起——
        // 整个脚本是 root 在跑：直接 nohup GUI 会因 PATH 缺失/环境不符秒退；
        // pkexec 注入 PKEXEC_UID（调用者 uid），用 runuser 回到原用户身份启动，
        // 且新版输出追加进 install.log（启动即崩时可诊断）。
        var logPath = Path.Combine(workDir, "install.log");
        var script = $"""
            #!/bin/sh
            exec >> '{EscapeSingle(logPath)}' 2>&1
            echo "== install start $(date) pid={Environment.ProcessId}"
            echo "DISPLAY=$DISPLAY WAYLAND_DISPLAY=$WAYLAND_DISPLAY"
            while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.3; done
            echo "app exited; running: {installCmd}"
            {installCmd}
            echo "install exit=$?"
            if [ -n "$PKEXEC_UID" ]; then
              REL_USER="$(getent passwd "$PKEXEC_UID" 2>/dev/null | cut -d: -f1)"
              echo "relaunch as uid=$PKEXEC_UID user=$REL_USER"
              runuser -u "$REL_USER" -- env DISPLAY="$DISPLAY" WAYLAND_DISPLAY="$WAYLAND_DISPLAY" \
                XAUTHORITY="$XAUTHORITY" XDG_RUNTIME_DIR="$XDG_RUNTIME_DIR" \
                DBUS_SESSION_BUS_ADDRESS="$DBUS_SESSION_BUS_ADDRESS" \
                DSH_DESKTOP_DSH_HOME="{EscapeSingle(Environment.GetEnvironmentVariable(DevEnvironment.HomeOverrideEnv) ?? string.Empty)}" \
                nohup '{EscapeSingle(exePath)}' >> '{EscapeSingle(logPath)}' 2>&1 &
            else
              echo "relaunch as current user"
              nohup '{EscapeSingle(exePath)}' >> '{EscapeSingle(logPath)}' 2>&1 &
            fi
            """;
        Directory.CreateDirectory(workDir);
        var scriptPath = Path.Combine(workDir, "install.sh");
        File.WriteAllText(scriptPath, script + Environment.NewLine);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // pkexec 会重置环境：显式透传 GUI 会话变量（否则拉起的新版窗口起不来）
        // 与开发隔离变量（否则重启后的实例丢掉 DSH_HOME 隔离，退回真实 home）
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
