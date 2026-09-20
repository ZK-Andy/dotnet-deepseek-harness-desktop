using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 系统默认浏览器打开（纯静态，可共享）：桌面壳把 WebView 内的站外 http(s) URL
/// 交给系统默认浏览器，而不是让它把页面导航走。Linux 走 <c>xdg-open</c>（重定向
/// 子进程输出，浏览器后台报错不回灌应用终端），其余平台 <c>Process.Start(UseShellExecute=true)</c>。
/// </summary>
/// <remarks>
/// 单一事实源：<see cref="ExternalLinkCommandRouter"/> 与 <see cref="RynNavigationCallbacks"/>
/// 的默认打开器都收敛到这里，避免两份复制打开逻辑漂移。
/// Linux 优先经 <c>systemd-run --user --scope</c> 承载（ADR exit-app-scope-ghost-residue）：
/// 浏览器整棵进程树生在独立 transient scope 里，不落进本壳的 GNOME app scope。不这么做的实机后果——
/// GLib 的 systemd-scope 启动只搬浏览器主 pid，启动瞬间 fork 的 helper 留在父进程 cgroup，
/// 把已退出的壳 scope 钉成幽灵（实机：17 个 Chrome helper / 1.5 GB 常驻）。
/// </remarks>
public static class SystemBrowser
{
    /// <summary>Linux 承载外链打开的 launcher 命令名：探到才用 scope 形态，缺失即回退直启。</summary>
    internal const string ScopeLauncherCommand = "systemd-run";

    /// <summary>scope 形态的固定参数前缀（命令与 URL 随后追加）。</summary>
    private static readonly string[] s_scopeArgs = ["--user", "--scope", "--collect", "--quiet", "--", "xdg-open"];

    /// <summary>失败路径取 stderr 末行的有界等待（浏览器若持有同一管道则拿不到尾行，不阻塞调用方）。</summary>
    private const int StderrWaitMs = 200;

    /// <summary>用系统默认浏览器打开 <paramref name="url"/>。返回是否已打开（已启动，或已成功交棒既有实例）。</summary>
    public static bool Open(string url)
    {
        string? scopeLauncher = ResolveScopeLauncher();
        (bool Ok, string? Diagnostic) first = Start(BuildProcessStartInfo(url, scopeLauncher));
        if (first.Ok || scopeLauncher is null)
        {
            return first.Ok;
        }

        // scope 形态失败（未启动或非零退出，如 user manager 不可达）：回退直启——可用性优先于 cgroup 卫生，
        // 代价是这一次打开会把浏览器树落回本壳 cgroup；失败原因随日志落盘（否则该形态为何不可用无从判断）
        HostLog.Write(
            $"[external-link] {ScopeLauncherCommand} scope 启动失败（{first.Diagnostic ?? "原因未知"}），回退直启 xdg-open：{url}");
        return Start(BuildProcessStartInfo(url, scopeLauncher: null)).Ok;
    }

    /// <summary>启动并折算结果：<c>Ok</c> = 已启动或已成功交棒（退出码 0）；<c>Ok=false</c> 时 <c>Diagnostic</c>
    /// 给出「未能启动 / 退出码 / 末行 stderr」的可读原因。</summary>
    private static (bool Ok, string? Diagnostic) Start(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi);
        if (p is null)
        {
            return (false, "未能启动进程");
        }

        var tail = new StderrTail();
        if (OperatingSystem.IsLinux())
        {
            // 必须持续排空重定向缓冲区，否则子进程写满管道会阻塞：stdout 刻意丢弃，stderr 末行同时留作失败诊断
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, e) => tail.Remember(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        // 非阻塞判据：未退出即按已启动；已退出按退出码判成败——xdg-open/systemd-run 交棒成功即 0，
        // 而「未退出才算成功」会把常见的快速交棒误报成打开失败（页面上白弹一次失败提示）
        if (!p.HasExited || p.ExitCode == 0)
        {
            return (true, null);
        }

        // 失败诊断取末行：等异步读处理完（有界）
        p.WaitForExit(StderrWaitMs);
        return (false, tail.Last is { Length: > 0 } line ? $"退出码 {p.ExitCode}：{line}" : $"退出码 {p.ExitCode}");
    }

    /// <summary>stderr 末行持有器：异步 handler 写、失败路径读（volatile 保证跨线程可见）。</summary>
    private sealed class StderrTail
    {
        private volatile string? _last;

        /// <summary>已捕获的最后一条非空 stderr 行；尚无则 null。</summary>
        internal string? Last => _last;

        /// <summary>记住一条 stderr 行（空行忽略）。</summary>
        /// <param name="line">stderr 行。</param>
        internal void Remember(string? line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                _last = line;
            }
        }
    }

    /// <summary>解析 Linux 上的 scope launcher 绝对路径；非 Linux 或 PATH 上探不到返回 null（= 直启形态）。</summary>
    internal static string? ResolveScopeLauncher() =>
        OperatingSystem.IsLinux()
            ? FindOnPath(ScopeLauncherCommand, Environment.GetEnvironmentVariable("PATH"), File.Exists)
            : null;

    /// <summary>在 PATH 各段中查找命令（存在性探测注入，纯函数可测）。</summary>
    /// <param name="command">命令名（不含目录）。</param>
    /// <param name="pathEnv">PATH 原文；null 或空串视为未命中。</param>
    /// <param name="exists">候选路径存在性探测。</param>
    /// <returns>首个命中的候选路径；未命中返回 null。</returns>
    internal static string? FindOnPath(string command, string? pathEnv, Func<string, bool> exists)
    {
        string[] dirs = (pathEnv ?? string.Empty).Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string dir in dirs)
        {
            string candidate = Path.Combine(dir, command);
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>构造打开命令的 <see cref="ProcessStartInfo"/>（纯函数，可单测）：Linux 走 <c>xdg-open</c>
    /// （<paramref name="scopeLauncher"/> 非 null 时经它包成独立 transient scope）并重定向子进程输出
    /// （浏览器后台报错不回灌应用终端），其余平台 <c>Process.Start(UseShellExecute=true)</c>。</summary>
    /// <param name="url">要打开的站外 http(s) URL。</param>
    /// <param name="scopeLauncher">Linux scope launcher 路径；null = 直启 xdg-open。</param>
    internal static ProcessStartInfo BuildProcessStartInfo(string url, string? scopeLauncher)
    {
        var psi = new ProcessStartInfo();
        if (!OperatingSystem.IsLinux())
        {
            psi.FileName = url;
            psi.UseShellExecute = true;
            return psi;
        }

        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (scopeLauncher is null)
        {
            psi.FileName = "xdg-open";
            psi.ArgumentList.Add(url);
            return psi;
        }

        // unit 名取 systemd 默认（run-*.scope），刻意不伪装 app-*：与「本壳实例」在 cgtop 里不混淆；
        // --collect 回收空/失败 unit（systemd ≥236），--quiet 去掉 systemd-run 自己的提示行
        psi.FileName = scopeLauncher;
        foreach (string arg in s_scopeArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add(url);
        return psi;
    }
}
