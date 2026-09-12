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

    /// <summary>探针执行器（ADR plugin-install-health-probe）：spawn 后不等服务完成，只等 <c>dsh web:</c>
    /// URL（超时/早退即败），finally 整树击杀。防御不变量对齐 RunAsync/RunStreamingAsync——OCE 会跳过
    /// 等待、using dispose 只关句柄不杀进程，取消/异常路径必须击杀，否则探针 dsh 成带血统 token 的孤儿
    /// （残留虽可被下次启动收割，但当次会占端口）。</summary>
    /// <param name="psi">探针启动信息（<see cref="PluginInstallProbe.BuildProbePsi"/> 产物）。</param>
    /// <param name="ct">取消令牌；取消以 OCE 上抛（探针随调用链路收口），仅超时按失败返回 null。</param>
    /// <returns>探针给出的 <c>dsh web:</c> URL；超时或早退返回 null。</returns>
    internal static async Task<Uri?> RunProbeAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
    {
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动探针 dsh 进程");
        var urlSignal = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && HarnessUrlParser.TryParse(e.Data) is { } url)
            {
                urlSignal.TrySetResult(url);
            }
        };
        // Exited 订阅先于 EnableRaisingEvents：启用后再订阅会漏掉两行之间发生的早退，
        // urlSignal 将无人置位、探针烧满 60s 超时（坏插件秒退场景必踩）
        p.Exited += (_, _) => urlSignal.TrySetResult(null);
        p.EnableRaisingEvents = true;
        p.BeginOutputReadLine();
        // 双流并发读排空 stderr：探针失败时 dsh 的错误输出可超 pipe buffer（~64KB），不排空会互等死锁
        p.BeginErrorReadLine();
        try
        {
            return await urlSignal.Task.WaitAsync(PluginInstallProbe.ProbeTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            HostLog.Write($"[host] 插件体检探针超时（{PluginInstallProbe.ProbeTimeout.TotalSeconds:s}s 未出 URL），回收探针进程");
            return null;
        }
        finally
        {
            KillTree(p);
        }
    }
}
