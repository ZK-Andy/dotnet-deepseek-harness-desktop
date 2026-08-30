using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主启动时的孤儿 dsh 清扫（ADR self-update-exit-reaps-dsh-child，缺口 B）。
/// </summary>
/// <remarks>
/// 背景：宿主进程异常死亡（被 SIGKILL/崩溃/非走退出编排）时，其 dsh 子进程不被收割，成为孤儿
/// 被 systemd --user 收养，继续占住首选端口（v0.3.11 实机：70587/71537 的 PPID = systemd --user）。
/// 缺口 A（自更新兜底收割）只覆盖宿主优雅退出；本清扫器覆盖宿主异常死亡残留。
///
/// 安全核心理念：**零误杀**。绝不裸用 PID 匹配——PID 复用会指向完全无关的进程，误杀不可逆。
/// 记录 spawn 时注入的 <c>DSH_DESKTOP_SPAWN_TOKEN</c>（唯一 GUID，经进程环境变量携带），清扫时
/// 复验该 PID 的进程环境里是否带同一个 token：匹配才是我们记录的 dsh（安全杀其进程树），
/// 不匹配/读不到（PID 复用/非 Linux 可读环境）则只记日志、绝不杀——端口漂移告警兜底。
///
/// 跨平台可测：核心判定 <see cref="Decide"/> 接受注入的 <c>readToken</c>（pid→token）与
/// <c>killTree</c>（pid→void）委托，纯逻辑可 xunit 单测；生产封装 <see cref="Reap"/> 用
/// Linux <c>/proc/&lt;pid&gt;/environ</c> 读 token、<c>Process.Kill(entireProcessTree)</c> 杀树。
/// </remarks>
public static class OrphanDshReaper
{
    /// <summary>spawn 时写入的子进程环境变量名（宿主经 <c>ProcessStartInfo.Environment</c> 注入）。</summary>
    public const string TokenEnv = "DSH_DESKTOP_SPAWN_TOKEN";

    /// <summary>读取一次 spawn 的 (pid, token)：PID 文件不存在/损坏 → null（无可清扫，静默）。</summary>
    /// <param name="pidPath">PID 文件路径（profiles/desktop/.dsh-pid）。</param>
    public static (int Pid, string Token)? ReadSpawnRecord(string pidPath)
    {
        try
        {
            if (!File.Exists(pidPath))
            {
                return null;
            }

            string[] lines = File.ReadAllLines(pidPath);
            if (lines.Length < 2)
            {
                return null;
            }

            return (int.Parse(lines[0].Trim()), lines[1].Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            // 记录损坏不可读：按无可清扫处理（fail-safe——清扫是增强，绝不挡启动）
            Services.HostLog.Write($"[host] 读 dsh PID 记录失败（跳过清扫）：{ex.Message}");
            return null;
        }
    }

    /// <summary>启动时清扫孤儿 dsh：读上次 spawn 记录，若该 PID 进程仍带同一 token 则整树杀之。</summary>
    /// <param name="pidPath">PID 文件路径。</param>
    /// <param name="readToken">读某 PID 进程环境的 token（注入；测试可伪造）。</param>
    /// <param name="killTree">杀某 PID 的整棵进程树（注入；测试可记录）。</param>
    /// <param name="log">日志（可选）。</param>
    /// <returns>true=已清扫一个孤儿；false=无孤儿或记录缺失/不匹配。</returns>
    /// <remarks>判定唯一依据 = token 复验匹配；任何无法复验的情形都按「不杀」处理，保证零误杀。</remarks>
    public static bool Reap(string pidPath, Func<int, string?> readToken, Action<int> killTree, Action<string>? log = null)
    {
        (int Pid, string Token)? record = ReadSpawnRecord(pidPath);
        if (record is null)
        {
            return false;
        }

        (int pid, string? token) = record.Value;
        string? current = readToken(pid);
        if (current != token)
        {
            // PID 复用指向无关进程 / 进程已死 / 非 Linux 读不到环境——一律不杀，漂移告警兜底
            log?.Invoke($"[host] 孤儿 dsh 清扫跳过：pid {pid} token 不复验（进程已退出或为无关进程）");
            return false;
        }

        log?.Invoke($"[host] 清扫孤儿 dsh：pid {pid} token 复验通过，整树击杀");
        try
        {
            killTree(pid);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[host] 清扫孤儿 dsh 失败：pid {pid} {ex.Message}");
            return false;
        }
    }

    /// <summary>生产封装：<paramref name="readToken"/> 用 Linux <c>/proc/&lt;pid&gt;/environ</c>，其余平台读不到即 null（不杀）。</summary>
    public static Func<int, string?> ReadTokenLinux() => pid =>
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windows/macOS 无统一的进程环境读取；保守按「读不到」处理，绝不误杀
            return null;
        }

        try
        {
            string environ = File.ReadAllText($"/proc/{pid}/environ");
            return environ.Split('\0')
                .Select(kv => kv.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0] == TokenEnv)
                .Select(parts => parts[1])
                .FirstOrDefault();
        }
        catch (Exception)
        {
            // 进程已死/无权限读：读不到 token → 不杀（零误杀）
            return null;
        }
    };

    /// <summary>生产封装：<paramref name="killTree"/> 用 .NET 整树击杀（跨平台）。</summary>
    public static Action<int> KillTreeProcessTree() => pid =>
    {
        using var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: true);
    };
}
