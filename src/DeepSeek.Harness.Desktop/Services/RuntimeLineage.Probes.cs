using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// <see cref="RuntimeLineage"/> 的生产探针面（partial）：<c>/proc</c> 读取、进程树击杀、回环端口探活。
/// 判定逻辑（分类/树内排除/处置计划）留在 <c>RuntimeLineage.cs</c>，本文件只做外部世界交互，
/// 故列在 D005 边界白名单（verify-code-conventions）。
/// </summary>
public static partial class RuntimeLineage
{
    /// <summary>端口探活的单次超时（TCP 连接本机回环：连不上即无人监听）。</summary>
    private static readonly TimeSpan s_portProbeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>枚举当前所有携带血统 token 的进程快照（Linux <c>/proc</c>；其他平台或读不到环境一律返回空）。</summary>
    /// <returns>候选快照列表；空表示无血统进程（也包含「平台不支持读取」这一保守退化）。</returns>
    /// <remarks>逐 pid 读 <c>/proc/&lt;pid&gt;/environ</c> 是唯一能发现血统进程的手段：先做 token 子串粗筛，
    /// 命中才解析 env 与命令行；读不到（他人进程/已退出）静默跳过，绝不因此误判为血统。</remarks>
    public static IReadOnlyList<Candidate> Enumerate()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windows/macOS 无统一的进程环境读取：保守返回空（清扫退回 .dsh-pid 记录路径）
            return [];
        }

        var candidates = new List<Candidate>();
        try
        {
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int pid))
                {
                    continue;
                }

                Candidate? candidate = TryReadCandidate(pid);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // /proc 不可遍历（受限容器）：本次扫描按无残留处理——观测位（漂移告警）兜底
            Services.HostLog.Write($"[host] 血统扫描无法遍历 /proc（跳过）：{ex.Message}");
        }

        return candidates;
    }

    /// <summary>读某进程 env 里的血统 token（<see cref="TokenEnv"/>）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>token；进程已死/无权限/非 Linux 返回 null（调用方按「不匹配」处理，零误杀）。</returns>
    public static string? ReadToken(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return EnvValue(File.ReadAllText($"/proc/{pid}/environ"), TokenEnv);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 进程已死/无权限读：读不到 token → 不杀（零误杀）
            return null;
        }
    }

    /// <summary>读某进程的父进程 id（<c>/proc/&lt;pid&gt;/stat</c> 第 4 字段，comm 含空格故从最后一个 ')' 起切）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>父 pid；读不到返回 null（调用方按父链断裂处理）。</returns>
    public static int? ReadParentPid(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int close = stat.LastIndexOf(')');
            if (close < 0)
            {
                return null;
            }

            string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0] = state，fields[1] = ppid
            return fields.Length > 1 && int.TryParse(fields[1], out int parentPid) ? parentPid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>整树击杀某 pid（残留收割与在管运行时回收共用；.NET 在 Unix 上即 SIGKILL）。</summary>
    /// <param name="pid">目标进程 id。</param>
    public static void KillTree(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
    }

    /// <summary>判定某 pid 是否仍在运行（收养的运行时的判活手段——它非本进程子进程，挂不上 Exited 事件）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>仍在运行返回 true；不存在或无法判定返回 false（走恢复路径）。</returns>
    /// <remarks>Linux 用 <c>/proc/&lt;pid&gt;</c> 存在性判定；其他平台退化为 <see cref="Process"/> 句柄判定。
    /// 当前调用图里只有 Linux 收养路径可达（<see cref="Enumerate"/> 非 Linux 恒空 ⇒ 不收养），非 Linux 分支
    /// 作为边界组件 API 的跨平台完备性保留。</remarks>
    public static bool TryIsAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.Exists($"/proc/{pid}");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>探测本机回环某端口此刻是否有监听者（TCP 连接成功即视为有人服务）。</summary>
    /// <param name="port">端口。</param>
    /// <param name="timeout">单次探测超时；传 null 用 <see cref="s_portProbeTimeout"/>。</param>
    /// <returns>有人监听返回 true。</returns>
    public static async Task<bool> IsLoopbackServingAsync(int port, TimeSpan? timeout = null)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(timeout ?? s_portProbeTimeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>读取单个 pid 的血统候选快照；非血统（无 token）或不可读返回 null。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>候选快照或 null。</returns>
    private static Candidate? TryReadCandidate(int pid)
    {
        try
        {
            string environ = File.ReadAllText($"/proc/{pid}/environ");
            string? token = EnvValue(environ, TokenEnv);
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            string cmdLine = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim();
            return new Candidate(
                pid,
                token,
                EnvValue(environ, HarnessRuntimeHost.EcosystemHomeEnv),
                cmdLine,
                TryReadStartTime(pid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 进程已死/无权限：不是血统候选（零误杀方向）
            return null;
        }
    }

    /// <summary>读进程起始时刻（用于区分「接力续任者」与「更早的残留实例」）。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>起始时刻；读不到返回 null（按不可证新生处理）。</returns>
    private static DateTimeOffset? TryReadStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>从 <c>/proc/&lt;pid&gt;/environ</c> 内容取某环境变量值（NUL 分隔的 <c>NAME=VALUE</c> 序列）。</summary>
    /// <param name="environ">environ 原文。</param>
    /// <param name="name">变量名。</param>
    /// <returns>取值；不存在返回 null。</returns>
    private static string? EnvValue(string environ, string name)
    {
        string prefix = name + "=";
        foreach (string entry in environ.Split('\0'))
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry[prefix.Length..];
            }
        }

        return null;
    }
}
