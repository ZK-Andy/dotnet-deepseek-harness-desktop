using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 残留 dsh 下游 scope 收割：scope 名内嵌的 dsh 主进程 pid 已死（<c>/proc</c> 不存在）才
/// <c>systemctl --user stop</c>，其余一律不动。判据依据、取证与零误杀边界见
/// ADR dsh-sandbox-child-orphan-leak。
/// </summary>
public static class DshSubprocessScopeReaper
{
    /// <summary>上游 dsh 的下游 scope 命名形状（journal 实证）：内嵌拉起它的 dsh 主进程 pid 与 hex 后缀。</summary>
    internal static readonly Regex ScopeNamePattern = new(@"^dsh-subprocess-(\d+)-[0-9a-f]+\.scope$", RegexOptions.Compiled);

    /// <summary>单次 systemctl 调用的等待上限（收割位于生命周期门内的同步路径）。</summary>
    private const int s_waitTimeoutMs = 15_000;

    /// <summary>收割残留 scope 的生产入口：非 Linux 无 systemd user scope 语义，no-op。</summary>
    /// <param name="log">日志回调（可选）：收割/失败留痕 host.log。</param>
    /// <returns>成功 stop 的 scope 数。</returns>
    public static int Reap(Action<string>? log = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        return Reap(ListScopeUnits(log), pid => !Directory.Exists($"/proc/{pid}"), StopScope, log);
    }

    /// <summary>收割决策核心（判定 + 副作用注入，测试面）：owner pid 已死的 scope 才 stop。</summary>
    /// <param name="units">scope unit 名列表（生产入口来自 systemctl，测试注入）。</param>
    /// <param name="isOwnerDead">owner pid 已死判定（死 → true；pid 复用/仍活 → false，不收割）。</param>
    /// <param name="stopScope">stop 单个 scope 的副作用（生产 = <c>systemctl --user stop</c>）。</param>
    /// <param name="log">日志回调（可选）。</param>
    /// <returns>成功 stop 的 scope 数。</returns>
    internal static int Reap(IReadOnlyList<string> units, Func<int, bool> isOwnerDead, Action<string> stopScope, Action<string>? log)
    {
        int stopped = 0;
        foreach (string unit in units)
        {
            if (!TryParseOwnerPid(unit, out int ownerPid) || !isOwnerDead(ownerPid))
            {
                // 命名形状不符 / owner 仍活（另一实例在管或 pid 复用）：一律不动（零误杀）
                continue;
            }

            try
            {
                stopScope(unit);
                stopped++;
                log?.Invoke($"[host] 收割残留下游 scope：{unit}（owner dsh pid {ownerPid} 已死）");
            }
            catch (Exception ex)
            {
                // 收割是增强：单点失败留痕不阻断启动（对齐 OrphanDshReaper 的 fail-safe 风格）
                log?.Invoke($"[host] 收割 scope 失败（留痕不阻断）：{unit} {ex.Message}");
            }
        }

        return stopped;
    }

    /// <summary>从 scope unit 名解析 owner dsh pid；命名形状不符或 pid 溢出返回 false（调用方按不收割处理）。</summary>
    internal static bool TryParseOwnerPid(string unit, out int ownerPid)
    {
        ownerPid = 0;
        Match match = ScopeNamePattern.Match(unit);
        return match.Success && int.TryParse(match.Groups[1].Value, out ownerPid);
    }

    /// <summary>枚举当前活跃的 dsh 下游 scope unit 名（systemctl 的 glob 匹配，no-legend 首列即 unit 名）。</summary>
    private static IReadOnlyList<string> ListScopeUnits(Action<string>? log)
    {
        try
        {
            string stdout = RunSystemCtl(log, "list-units", "--no-legend", "--", "dsh-subprocess-*.scope");
            return stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', 2)[0])
                .ToList();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[host] 枚举 dsh 下游 scope 失败（跳过收割）：{ex.Message}");
            return [];
        }
    }

    /// <summary>stop 单个 scope unit；非零退出码视为失败抛出（由决策核心留痕）。</summary>
    private static void StopScope(string unit)
    {
        RunSystemCtl(null, "stop", unit);
    }

    private static string RunSystemCtl(Action<string>? log, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "systemctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--user");
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 systemctl。");

        // stderr 并发消费防双管道互等；等待有界（收割在 _lifecycleGate 门内，绝不长时间挂住 spawn 路径）
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(s_waitTimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"systemctl {args[0]} {s_waitTimeoutMs}ms 未退出，已终止");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        return process.ExitCode == 0
            ? stdout
            : throw new InvalidOperationException($"systemctl {args[0]} 退出码 {process.ExitCode}：{stderrTask.Result.Trim()}");
    }
}
