using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 跨进程文件锁持有者：起一个**独立进程**以 OS 级锁独占目标文件，验证 <c>FileShare.None</c> 的跨进程
/// 语义（同进程持有不构成他实例证据——<see cref="StalePackagePrunerTests"/> 既有模拟的盲区）。
/// 机制按 OS 对齐 .NET 运行时的实现：Unix 用 flock（python3 <c>fcntl.flock</c>），Windows 用 LockFile
/// （PowerShell <c>File.Open</c> FileShare.None）——已实测两机制都与 .NET <c>FileShare.None</c> 互斥
/// （ADR 补测批；python 的 fcntl.lockf/POSIX 锁与 .NET 互不相干，不可用）。
/// </summary>
internal static class ExternalFileLockHolder
{
    /// <summary>启动持有者并阻塞到其真正持锁（读 HELD 就绪行）。目标文件须已存在。</summary>
    /// <param name="path">要独占的文件全路径。</param>
    /// <returns>持有者进程（<see cref="Stop"/> 释放）。</returns>
    public static Process Start(string path)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            // 路径插在单引号字符串里：Windows 临时路径不含单引号，双单引号转义兜底
            psi = new ProcessStartInfo
            {
                FileName = "powershell",
                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    $"$f=[System.IO.File]::Open('{path.Replace("'", "''")}','Open','ReadWrite','None');"
                    + "[Console]::WriteLine('HELD'); Start-Sleep -Seconds 60; $f.Dispose()",
                },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "python3",
                ArgumentList =
                {
                    "-c",
                    "import fcntl,sys,time; f=open(sys.argv[1],'r+'); fcntl.flock(f, fcntl.LOCK_EX|fcntl.LOCK_NB);"
                    + " print('HELD',flush=True); time.sleep(60)",
                    path,
                },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
        }

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("锁持有者子进程启动失败");
        string? ready;
        try
        {
            ready = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Stop(process);
            throw new InvalidOperationException("锁持有者未在 10s 内就绪（子进程异常挂起）");
        }

        if (ready != "HELD")
        {
            Stop(process);
            throw new InvalidOperationException($"锁持有者未就绪（读到：{ready ?? "<null>"}）");
        }

        return process;
    }

    /// <summary>终止持有者并等待退出（锁随进程死亡释放）。</summary>
    public static void Stop(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // 进程恰好已退出
        }

        // Kill（SIGKILL/TerminateProcess）已发出：退出是必然事件，无限等确保 Windows 上句柄释放后才清理目录
        process.WaitForExit();
        process.Dispose();
    }
}
