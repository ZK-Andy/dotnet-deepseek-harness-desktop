using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// <see cref="RuntimeBootstrap"/> 的引擎辅助面（partial，ADR 尺寸健康闸）：子进程捕获、全局 node 探测、
/// 步骤超时、失败态、npm-cli 定位。引导状态机（RunAsync/EnsureGlobalNodeAsync）与
/// 纯函数/路径单点面分别在 <c>RuntimeBootstrap.cs</c>/<c>RuntimeBootstrap.Pure.cs</c>。
/// </summary>
public static partial class RuntimeBootstrap
{
    private static async Task<(int Exit, string Stdout, string Stderr)> RunCaptureAsync(
        Action<string> log, string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = StripExtendedPrefix(exe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // GUI 子系统壳 spawn node/npm 不能闪控制台窗（Windows）
            CreateNoWindow = true,
        };
        HarnessRuntimeHost.UseUtf8TextStreams(psi);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // npm 专属：显式压低 V8 堆上限。dsh 依赖树（454 包）解析峰值 ~1.7-3GB，默认上限
        // （≈物理内存一半）在弱内存机器会 abort（exit 134，沙箱 8G 实证）；3072 强制积极 GC
        // 且留足解析空间。仅对 npm-cli 调用注入，不污染 node 运行 dsh 的堆行为
        if (args.Any(a => a.EndsWith("npm-cli.js", StringComparison.Ordinal)))
        {
            psi.Environment["NODE_OPTIONS"] = "--max-old-space-size=3072";
        }

        log?.Invoke($"[bootstrap] run: {psi.FileName} {string.Join(' ', args)}");
        using Process p = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程 {exe}");
        try
        {
            // 双流并发读：顺序先读 stdout 时 stderr 塞满 pipe buffer（~64KB）会互等死锁
            // （npm 崩溃 full report 可超限）。进程退出后两流必然收尾，收尾读取不再带 ct
            Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return (p.ExitCode, stdout, stderr);
        }
        catch (Exception)
        {
            // 取消/异常路径必须整树击杀：ReadToEndAsync 的 OCE 会跳过 WaitForExitAsync（其内部
            // 才注册 kill-on-cancel），using dispose 只关句柄不杀进程——npm 会带着写权成孤儿，下次
            // 引导与新进程竞写 node_modules
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已自行退出：无需击杀
            }

            throw;
        }
    }

    /// <summary>探测 PATH 上系统全局 node（取真实可执行路径）+ 其 npm-cli.js。全局 node 是那份唯一 dsh 的运行时，
    /// 桌面与终端共用（ADR simple-shell-single-global-dsh）。</summary>
    private static async Task<(string? NodePath, string? NpmCli)> ProbeLocalNodeAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            // 确认 node 可执行（--version 非零即视为不存在），并取真实路径（process.execPath），
            // npm-cli 紧邻 node 安装在发行包布局内。
            (int exit, string? stdout, string _) = await RunCaptureAsync(log, "node", ["-e", "console.log(process.execPath)"], ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return (null, null);
            }

            string? nodePath = stdout?.Trim();
            if (string.IsNullOrWhiteSpace(nodePath))
            {
                return (null, null);
            }

            // npm-cli 定位失败即视为本机 node 不可复用（绝不猜测执行）
            string? npmCli = LocateNpmCliBesideLocalNode(nodePath);
            return npmCli is null ? (null, null) : (nodePath, npmCli);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke($"[bootstrap] PATH node 探测失败（视为不存在）：{ex.Message}");
            return (null, null);
        }
    }

    /// <summary>步骤级超时包装：应用退出（appCt）取消仍以 OCE 上抛；仅步超时转异常走失败重试页。</summary>
    private static Task WithStepTimeout(int minutes, CancellationToken appCt, Func<CancellationToken, Task> action) =>
        WithStepTimeoutAsync<object?>(minutes, appCt, async token =>
        {
            await action(token).ConfigureAwait(false);
            return null;
        });

    private static async Task<T> WithStepTimeoutAsync<T>(int minutes, CancellationToken appCt, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        cts.CancelAfter(TimeSpan.FromMinutes(minutes));
        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!appCt.IsCancellationRequested)
        {
            throw new InvalidOperationException($"步骤超时（>{minutes} 分钟），网络停滞或资源受限，可重试");
        }
    }

    private static BootstrapOutcome Fail(BootstrapStep step, string error)
    {
        // 失败进度由调用方在重试循环推送（此处返回即可，避免双通道）
        return new BootstrapOutcome(false, step, $"[{step}] {error}", null);
    }

    private static string? LocateNpmCliBesideLocalNode(string nodePath)
    {
        // node 在 PATH 上时布局不定（发行包/包管理器/symlink）；只探测两种主流布局，
        // 找不到 npm-cli 一律视为不可复用——绝不猜测执行
        string? dir = Path.GetDirectoryName(Path.GetFullPath(nodePath));
        if (dir is null)
        {
            return null;
        }

        string[] candidates = new[]
        {
            // 发行包布局：node 同级的 lib/node_modules（unix）或同级 node_modules（win）
            Path.Combine(dir, NpmCliRelativePath()),
            Path.Combine(dir, "..", NpmCliRelativePath()),
            Path.Combine(dir, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
