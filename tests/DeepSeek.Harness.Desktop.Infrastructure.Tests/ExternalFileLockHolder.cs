using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests;

/// <summary>
/// 跨进程文件锁持有者：起一个**独立进程**以 OS 级锁独占目标文件，验证 <c>FileShare.None</c> 的跨进程
/// 语义（同进程持有不构成他实例证据——<see cref="Update.StalePackagePrunerTests"/> 既有模拟的盲区）。
/// 机制按 OS 对齐 .NET 运行时的实现：Unix 用 flock（python3 <c>fcntl.flock</c>），Windows 用 LockFile
/// （PowerShell <c>File.Open</c> FileShare.None）——已实测两机制都与 .NET <c>FileShare.None</c> 互斥
/// （ADR 补测批；python 的 fcntl.lockf/POSIX 锁与 .NET 互不相干，不可用）。
/// 就绪以子进程 stdout 的 HELD 行为准；stderr 自启动起持续排空，失败异常携带 stderr 尾部、进程状态
/// 与就绪预算——CI 冷启动失败要能直接定位，而不是只剩一条「未就绪」。
/// </summary>
internal static class ExternalFileLockHolder
{
    // 就绪预算：原 10s 在 CI（windows-latest）实测被突破——powershell 冷启动叠加负载时读不到 HELD；
    // 30s 给 powershell/python3 冷启动留余量，正常路径仍在毫秒级返回，不拖慢测试。
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromSeconds(30);

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
                RedirectStandardError = true,
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
                RedirectStandardError = true,
                UseShellExecute = false,
            };
        }

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("锁持有者子进程启动失败");

        // 启动即持续排空 stderr：既不因管道缓冲写满与 HELD 输出互锁，也让失败诊断拿得到子进程真实报错。
        Task<string> stderrDrain = process.StandardError.ReadToEndAsync();

        string? ready;
        try
        {
            ready = process.StandardOutput.ReadLineAsync().WaitAsync(s_readyTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                DescribeFailure(process, stderrDrain, "未在就绪预算内产出 HELD（子进程异常挂起）"));
        }

        if (ready != "HELD")
        {
            throw new InvalidOperationException(
                DescribeFailure(process, stderrDrain, $"未就绪（读到：{ready ?? "<null>"}）"));
        }

        return process;
    }

    /// <summary>终止持有者并等待退出（锁随进程死亡释放）。</summary>
    public static void Stop(Process process)
    {
        Kill(process);

        // Kill（SIGKILL/TerminateProcess）已发出：退出是必然事件，无限等确保 Windows 上句柄释放后才清理目录
        process.WaitForExit();
        process.Dispose();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // 进程恰好已退出
        }
    }

    // 失败收口：先记失败时的进程状态，再终止并收 stderr 尾部（进程退出后管道 EOF 才能读完，Dispose 归此路径）。
    private static string DescribeFailure(Process process, Task<string> stderrDrain, string reason)
    {
        bool wasRunning = !process.HasExited;
        Kill(process);
        process.WaitForExit();
        string stderrTail = StderrTail(stderrDrain);
        string state = wasRunning
            ? $"失败时仍在运行、已终止（退出码 {process.ExitCode}）"
            : $"失败前已退出（退出码 {process.ExitCode}）";
        process.Dispose();
        return $"锁持有者{reason}；就绪预算 {s_readyTimeout.TotalSeconds:0}s；进程{state}；"
            + $"stderr 尾部：{(stderrTail.Length == 0 ? "<空>" : stderrTail)}";
    }

    // stderr 尾部：末 20 行且不超过 800 字（CI 日志窗口有限，诊断够用即可）。
    // 进程已终止：ReadToEndAsync 随管道 EOF 完成；短等兜底防诊断路径自己挂住。任务以 faulted/canceled
    // 收尾时 Wait 会抛 AggregateException（不返回 false），故单捕捉——诊断路径不能自己抛裸异常。
    private static string StderrTail(Task<string> stderrDrain)
    {
        string text;
        try
        {
            text = stderrDrain.Wait(TimeSpan.FromSeconds(2))
                ? stderrDrain.Result
                : "<stderr 读取超时>";
        }
        catch (AggregateException)
        {
            text = "<stderr 读取失败>";
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string tail = string.Join('\n', lines.TakeLast(20));
        return tail.Length <= 800 ? tail : tail[^800..];
    }
}
