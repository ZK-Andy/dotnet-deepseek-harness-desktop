using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 子进程执行器（ADR 组合根只装配）：spawn、双流读、逐行转发、取消/异常整树击杀集中在此，
/// 组合根（<c>DesktopBootstrap</c>）不再直跑进程。形态对齐 <c>RuntimeBootstrap.RunCaptureAsync</c>
/// 的防御不变量——<c>WaitForExitAsync</c>/<c>ReadLineAsync</c> 的 OCE 会跳过等待、using dispose
/// 只关句柄不杀进程，取消/异常必须整树击杀，否则 <c>dsh plugin add</c> 带 profile 写权成孤儿。
/// </summary>
internal static class PluginProcessRunner
{
    /// <summary>执行一个子进程（读满输出），返回 (exit, stdout, stderr)。</summary>
    internal static async Task<(int Exit, string Out, string Err)> RunAsync(
        System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
    {
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dsh plugin 进程");
        Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception)
        {
            KillTree(p);
            throw;
        }
    }

    /// <summary>流式执行器：逐行读 stdout/stderr，每行经 <paramref name="onLine"/> 转发（页面日志回流），
    /// 同时累积完整输出供调用方判定。fire-and-forget 的页面推送故障由调用方吞，绝不抛入执行器。</summary>
    internal static async Task<(int Exit, string Out, string Err)> RunStreamingAsync(
        System.Diagnostics.ProcessStartInfo psi, CancellationToken ct, Action<string>? onLine)
    {
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dsh plugin 进程");
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();

        try
        {
            Task[] tasks = new[] { PumpAsync(p.StandardOutput, outSb, onLine, ct), PumpAsync(p.StandardError, errSb, onLine, ct) };
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return (p.ExitCode, outSb.ToString(), errSb.ToString());
        }
        catch (Exception)
        {
            KillTree(p);
            throw;
        }
    }

    /// <summary>取消/异常路径整树击杀进程树：ReadToEndAsync/WaitForExit 的 OCE 会跳过等待，
    /// using dispose 只关句柄不杀进程——不杀则进程带写权成孤儿（RunAsync/RunStreamingAsync 共用）。
    /// 已自行退出的进程不重复击杀。调用方在 catch 内负责重抛。</summary>
    private static void KillTree(System.Diagnostics.Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 进程已自行退出：无需击杀
        }
    }

    /// <summary>逐行泵出进程流（测试可注入内存流验证行转发与累积；生产经进程 stdout/stderr）。
    /// 取消由调用方经 <paramref name="ct"/> 传递。</summary>
    internal static async Task PumpAsync(
        System.IO.StreamReader reader, StringBuilder sb, Action<string>? onLine, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            sb.AppendLine(line);
            onLine?.Invoke(line);
        }
    }
}
