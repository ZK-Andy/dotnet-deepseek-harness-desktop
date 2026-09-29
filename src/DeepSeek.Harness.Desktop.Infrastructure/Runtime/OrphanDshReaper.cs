namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 宿主启动时的孤儿 dsh 清扫（ADR self-update-exit-reaps-dsh-child，缺口 B）——<c>.dsh-pid</c> 记录路径，
/// 跨平台可用。血统扫描路径（记录被后续 spawn 覆盖后仍能收敛）见 <see cref="RuntimeLineage"/>。
/// </summary>
/// <remarks>
/// 背景：宿主进程异常死亡（被 SIGKILL/崩溃/非走退出编排）时，其 dsh 子进程不被收割，成为孤儿
/// 被 systemd --user 收养，继续占住首选端口（v0.3.11 实机：70587/71537 的 PPID = systemd --user）。
/// 缺口 A（自更新兜底收割）只覆盖宿主优雅退出；本清扫器覆盖宿主异常死亡残留。
///
/// 安全核心理念：**零误杀**。绝不裸用 PID 匹配——PID 复用会指向完全无关的进程，误杀不可逆。
/// 记录 spawn 时注入的 <see cref="RuntimeLineage.TokenEnv"/>（唯一 GUID，经进程环境变量携带），清扫时
/// 复验该 PID 的进程环境里是否带同一个 token：匹配才是我们记录的 dsh（安全杀其进程树），
/// 不匹配/读不到则只记日志、绝不杀——若命令行可证该 PID 已被无关进程复用（<see cref="PlausiblyOwnRuntime"/>
/// 甄别），记录判过期按 Clear 收敛；其余情形（Windows/macOS 无 /proc 复验等）fail loud 挡 spawn，
/// 端口漂移告警兜底。
///
/// 跨平台可测：核心判定 <see cref="Reap"/> 接受注入的 <c>readToken</c>（pid→token）与
/// <c>killTree</c>（pid→void）委托，纯逻辑可 xunit 单测；生产由组合点直接注入
/// <see cref="RuntimeLineageProbes.ReadToken"/> 与 <see cref="RuntimeLineageProbes.KillTree"/>。
/// </remarks>
public static class OrphanDshReaper
{
    /// <summary>残留收敛结果（预检语义）：调用方据此决定 spawn / 跳过 / fail loud。</summary>
    internal enum ResidueState
    {
        /// <summary>无残留（无记录、记录不可读、或记录 pid 已死）：可直接 spawn。</summary>
        Clear,

        /// <summary>复验命中的残留已杀且确认死亡：可直接 spawn。</summary>
        Reaped,

        /// <summary>残留仍在但无法安全回收（杀后仍活 / 活着但验不明归属不敢杀）：不得 spawn，fail loud。</summary>
        Unreapable,
    }

    /// <summary>残留收敛预检（ADR residue-lock-fail-loud）：在既有 <see cref="Reap"/> 的 verified-kill
    /// 之上，补"杀不掉/不敢杀"的可区分结论——Windows/macOS 上 <c>readToken</c> 恒 null 导致旧
    /// <c>Reap</c> 静默跳过，调用方分不清"无残留"与"有残留但杀不了"，随后盲目 spawn 即端口碰撞。
    /// 零误杀：活着但复验不中的 pid 一律不杀，由调用方 fail loud（用户手动清理后下轮自动恢复）。</summary>
    /// <param name="pidPath">PID 文件路径（与 <see cref="Reap"/> 同）。</param>
    /// <param name="readToken">读某 PID 进程环境的 token（注入；生产用 <c>RuntimeLineageProbes.ReadToken</c>）。</param>
    /// <param name="isAlive">判某 PID 是否仍活（注入；生产用 <c>RuntimeLineageProbes.TryIsAlive</c>）。</param>
    /// <param name="killTree">杀某 PID 的整棵进程树（注入；生产用 <c>RuntimeLineageProbes.KillTree</c>）。</param>
    /// <param name="log">日志（可选；Unreapable 分支必留痕，fail loud）。</param>
    /// <param name="readCommandLine">读某 PID 的命令行（注入；生产用 <c>RuntimeLineageProbes.ReadCommandLine</c>，
    /// 可选——缺省时「活着但验不明」恒 Unreapable，即补丁前行为）。仅用于 PID 复用的他者甄别：
    /// 命令行明显不是自家 runtime 形态时判记录过期、按 Clear 收敛（见 <see cref="PlausiblyOwnRuntime"/>），
    /// 绝不据此杀进程。</param>
    /// <returns>收敛结论（<see cref="ResidueState"/>）。</returns>
    internal static ResidueState EnsureNoResidue(
        string pidPath,
        Func<int, string?> readToken,
        Func<int, bool> isAlive,
        Action<int> killTree,
        Action<string>? log = null,
        Func<int, string?>? readCommandLine = null)
    {
        (int Pid, string Token)? record = ReadSpawnRecord(pidPath);
        if (record is null)
        {
            return ResidueState.Clear;
        }

        (int pid, string token) = record.Value;
        bool verified;
        try
        {
            verified = readToken(pid) == token;
        }
        catch (Exception ex)
        {
            // 复验委托本身异常：按"验不明"处理，下面的判活分支收敛
            log?.Invoke($"[host] 残留复验异常（按验不明处理）：pid {pid} {ex.Message}");
            verified = false;
        }

        // 判活包装：异常一律按"仍活"收敛（fail loud 方向，不静默 spawn 碰撞），并留痕
        bool AliveOrLoud(string phase)
        {
            try
            {
                return isAlive(pid);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[host] 残留判活异常（按仍活处理，{phase}）：pid {pid} {ex.Message}");
                return true;
            }
        }

        if (verified)
        {
            // 命中即杀（复用既有 Reap：幂等、无记录竞态下按未杀处理，零误杀语义不变）。
            // Reap 内会重调 readToken（try 之外）：注入委托若二调抛则此处收敛为 Unreapable
            // （fail loud 方向；生产 ReadToken 自吞 IO/越权、非 Linux 返 null，实不抛）
            bool reaped;
            try
            {
                reaped = Reap(pidPath, readToken, killTree, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[host] 残留收割异常（按杀不掉处理）：pid {pid} {ex.Message}");
                return ResidueState.Unreapable;
            }

            if (AliveOrLoud("杀后复查"))
            {
                log?.Invoke($"[host] 残留无法回收：pid {pid} 复验命中但杀后仍活，不 spawn（fail loud，请手动结束该进程）");
                return ResidueState.Unreapable;
            }

            return reaped ? ResidueState.Reaped : ResidueState.Clear;
        }

        if (!AliveOrLoud("验不明复查"))
        {
            return ResidueState.Clear;
        }

        return ResolveUnverifiedAlive(pid, pidPath, log, readCommandLine);
    }

    /// <summary>「活着但验不明」的收敛：先做 PID 复用甄别（ADR pid-reuse-foreign-cmdline-clear）——命令行
    /// 可读且明显不是自家 runtime 形态，说明原记录的 dsh 早已不在（PID 被无关进程复用，跨重启实测：
    /// stale 记录 pid 撞上 GNOME localsearch-3），记录本身才是脏数据，清掉脏记录按 Clear 放行（绝不杀）；
    /// 甄别不了（cmdline 不可读/形态存疑/委托异常）保持 fail loud 不放行。</summary>
    /// <param name="pid">记录中的残留 pid。</param>
    /// <param name="pidPath">PID 文件路径（清脏记录用）。</param>
    /// <param name="log">日志（可选）。</param>
    /// <param name="readCommandLine">命令行探针（注入；null = 不甄别，恒 fail loud）。</param>
    /// <returns>Clear（已证记录过期）或 Unreapable（甄别不出）。</returns>
    private static ResidueState ResolveUnverifiedAlive(
        int pid,
        string pidPath,
        Action<string>? log,
        Func<int, string?>? readCommandLine)
    {
        if (readCommandLine is null)
        {
            return UnreapableLoud(pid, log);
        }

        string? commandLine;
        try
        {
            commandLine = readCommandLine(pid);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[host] 残留甄别读命令行异常（按验不明处理）：pid {pid} {ex.Message}");
            commandLine = null;
        }

        if (commandLine is not null && !PlausiblyOwnRuntime(commandLine))
        {
            log?.Invoke($"[host] 残留记录过期收敛：pid {pid} 已被无关进程复用（命令行：{commandLine}），清脏记录放行");
            TryClearStaleRecord(pidPath);
            return ResidueState.Clear;
        }

        return UnreapableLoud(pid, log);
    }

    /// <summary>验不明且不可放行的 fail-loud 留痕：绝不杀，调用方挡 spawn（用户手动确认清理后下轮自动恢复）。</summary>
    private static ResidueState UnreapableLoud(int pid, Action<string>? log)
    {
        log?.Invoke($"[host] 残留无法安全回收：pid {pid} 仍活但归属验不明，不敢杀、不 spawn（fail loud，请确认后手动结束该进程）");
        return ResidueState.Unreapable;
    }

    /// <summary>命令行是否形似自家 runtime（保守白名单：dsh 本体，或 node——dsh 经 shebang 由 node
    /// 执行，argv[0] 恒为 node）。只在「是否放行」上消费：形似自家或无法判定一律保持 Unreapable
    /// （fail loud 方向），即本谓词为 false 才是放行依据——把「像」误判成自家只是放弃一次放行，
    /// 把「不像」误判成他者则会跳过真实残留，故白名单取窄不取宽。</summary>
    /// <param name="commandLine">空格连接的命令行（<c>/proc/&lt;pid&gt;/cmdline</c> 折叠形）。</param>
    /// <returns>形似自家 runtime 返回 true。</returns>
    internal static bool PlausiblyOwnRuntime(string commandLine)
    {
        string[] argv = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0)
        {
            // 空形（内核线程等）不可证他者：按自家处理（保守不放行）
            return true;
        }

        string exe = Path.GetFileName(argv[0]);
        return exe is "node" or "dsh"
            || argv.Skip(1).Any(arg => Path.GetFileName(arg) is "dsh" or "dsh.cmd" or "dsh.exe");
    }

    /// <summary>清掉已被证伪的过期 PID 记录（尽力而为：删除失败不阻断放行，下轮重走同一甄别再收敛）。</summary>
    /// <param name="pidPath">PID 文件路径。</param>
    private static void TryClearStaleRecord(string pidPath)
    {
        try
        {
            File.Delete(pidPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostLog.Write($"[host] 清过期 dsh PID 记录失败（忽略，下轮重收敛）：{ex.Message}");
        }
    }
    /// <summary>读取一次 spawn 的 (pid, token)：PID 文件不存在/损坏 → null（无可清扫，静默）。</summary>
    /// <param name="pidPath">PID 文件路径（profiles/<see cref="HarnessRuntimeHost.DesktopProfileName"/>/.dsh-pid）。</param>
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
            HostLog.Write($"[host] 读 dsh PID 记录失败（跳过清扫）：{ex.Message}");
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
}
