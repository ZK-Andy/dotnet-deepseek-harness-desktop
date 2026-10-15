using System.ComponentModel;
using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 遗留原生对话框收割（Linux <c>/proc</c>；ADR failure-surface-dialog-defects）：退出收口时杀掉本进程的
/// 直接子进程中名为 zenity/kdialog 者（失败面原生模态的承载子进程，插件起、壳杀不掉——插件 0.38.0 无取消面）。
/// Windows 面是进程内 <c>MessageBoxW</c>（随进程同生共死）、macOS 无 <c>/proc</c> 判亲缘——两处 no-op
///（macOS 孤儿框记已知缺口，图标需求一并走上游）。读不到/杀不掉只留痕，不抛。
/// </summary>
internal sealed class DialogOrphanReaper : IDialogOrphanReaper
{
    private readonly Action<string>? _log;

    /// <summary>创建收割器。</summary>
    /// <param name="log">日志出口（可选）。</param>
    public DialogOrphanReaper(Action<string>? log = null)
    {
        _log = log;
    }

    /// <inheritdoc />
    public void Reap()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int self = Environment.ProcessId;
        int killed = 0;
        foreach (int pid in EnumerateDialogChildren(self))
        {
            if (TryKill(pid))
            {
                killed++;
            }
        }

        if (killed > 0)
        {
            _log?.Invoke($"[host] 退出收口：已收割遗留原生对话框子进程 {killed} 个");
        }
    }

    /// <summary>枚举本进程的直接子进程中名为对话框工具者（/proc 亲缘判定 + 进程名双条件，零误杀方向）。</summary>
    /// <param name="parentPid">本进程 id。</param>
    /// <returns>待收割 pid 列表。</returns>
    private IEnumerable<int> EnumerateDialogChildren(int parentPid)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories("/proc");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[host] 对话框孤儿扫描无法遍历 /proc（跳过）：{ex.Message}");
            yield break;
        }

        foreach (string directory in directories)
        {
            if (!int.TryParse(Path.GetFileName(directory), out int pid) || pid == parentPid)
            {
                continue;
            }

            if (TryReadDialogChild(directory, parentPid))
            {
                yield return pid;
            }
        }
    }

    /// <summary>判某 pid 是否为待收割的对话框子进程（读不到 stat 即按「不是」处理）。</summary>
    /// <param name="directory">该 pid 的 /proc 目录。</param>
    /// <param name="parentPid">本进程 id（亲缘以 stat 内 ppid 为准）。</param>
    /// <returns>true = 直接子进程且进程名为对话框工具。</returns>
    private static bool TryReadDialogChild(string directory, int parentPid)
    {
        string stat;
        try
        {
            stat = File.ReadAllText(Path.Combine(directory, "stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // 进程已死/无权限读：不是收割对象（零误杀方向）
        }

        return TryParseStat(stat, out string comm, out int ppid) && ppid == parentPid && IsDialogProcessName(comm);
    }

    /// <summary>解析 <c>/proc/&lt;pid&gt;/stat</c> 的进程名与父 pid（纯函数）：comm 取首 <c>(</c> 到末 <c>)</c>
    /// 之间（comm 可含空格与括号），ppid 取 <c>)</c> 之后第 2 个字段。</summary>
    /// <param name="stat">stat 文件原文。</param>
    /// <param name="comm">进程名（失败即空串）。</param>
    /// <param name="ppid">父 pid（失败即 0）。</param>
    /// <returns>解析成功。</returns>
    internal static bool TryParseStat(string stat, out string comm, out int ppid)
    {
        comm = string.Empty;
        ppid = 0;
        int open = stat.IndexOf('(');
        int close = stat.LastIndexOf(')');
        if (open < 0 || close <= open + 1)
        {
            return false;
        }

        comm = stat.Substring(open + 1, close - open - 1);
        string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && int.TryParse(fields[1], out ppid);
    }

    /// <summary>是否为失败面对话框承载进程名（zenity/kdialog：与插件候选序同源；macOS osascript 不在此列）。</summary>
    /// <param name="comm">stat 解析出的进程名。</param>
    /// <returns>true = 对话框工具进程。</returns>
    internal static bool IsDialogProcessName(string comm) => comm is "zenity" or "kdialog";

    /// <summary>杀进程（best-effort：已死/无权限只留痕）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>是否杀掉（发信号即算，回收由 init 接管）。</returns>
    private bool TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or NotSupportedException or Win32Exception)
        {
            _log?.Invoke($"[host] 对话框孤儿收割失败（跳过）：pid {pid}：{ex.Message}");
            return false;
        }
    }
}
