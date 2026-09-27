using System.Diagnostics;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// <see cref="RuntimeBootstrap"/> 的引擎辅助面（partial，ADR 尺寸健康闸）：子进程捕获、全局 node 探测、
/// 步骤超时、失败态、npm-cli 定位。引导状态机（RunAsync/EnsureGlobalNodeAsync）与
/// 纯函数/路径单点面分别在 <c>RuntimeBootstrap.cs</c>/<c>RuntimeBootstrap.Pure.cs</c>。
/// </summary>
public static partial class RuntimeBootstrap
{
    /// <summary>
    /// 构造引导子进程（node/npm/dsh）的启动信息：先剥离宿主继承噪声（ADR
    /// spawn-env-and-plugin-spec-hardening）——宿主 <c>npm_config_registry</c> 之类可改写全局安装的
    /// 解析来源（供应链面），<c>NODE_OPTIONS</c> 可注入任意代码。
    /// </summary>
    /// <param name="exe">可执行路径（允许 Windows 扩展前缀，内部剥离）。</param>
    /// <param name="args">参数列表。</param>
    /// <returns>已配好参数、环境净化与 npm 专属堆上限的启动信息。</returns>
    internal static ProcessStartInfo BuildCapturePsi(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = StripExtendedPrefix(exe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // stdin 显式重定向并在启动后立即关闭（见 RunCaptureAsync/RunStreamingCaptureAsync）：
            // GUI 进程继承的 stdin 句柄形态不定，子进程（npm/dsh）一旦读 stdin 即与宿主互等。
            // 竞品同款（hairyf unix `stdin(Stdio::null())` + Windows 隐藏控制台 spawn）。
            RedirectStandardInput = true,
            // GUI 子系统壳 spawn node/npm 不能闪控制台窗（Windows）
            CreateNoWindow = true,
        };
        EnvironmentHygiene.StripInherited(psi);
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

        return psi;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCaptureAsync(
        Action<string> log, string exe, IReadOnlyList<string> args, bool english, CancellationToken ct)
    {
        ProcessStartInfo psi = BuildCapturePsi(exe, args);

        log?.Invoke($"[bootstrap] run: {psi.FileName} {string.Join(' ', args)}");
        using Process p = Process.Start(psi)
            ?? throw new InvalidOperationException(UiCopy.BootstrapProcessStartFailed(exe, english));
        // stdin 已重定向即关闭写端：子进程读到 EOF 而非阻塞等宿主（见 BuildCapturePsi）。
        p.StandardInput.Close();
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

    /// <summary>流式行单行上限：npm 进度行（`\r` 刷新的单行 bar）可达数 KB，超限截断防日志爆炸。
    /// 经 <see cref="Plugins.PluginProcessRunner.PumpAsync"/> 的截断参数生效（R1 简化统一，无自有行泵）。</summary>
    internal const int MaxStreamLineChars = 300;

    /// <summary>
    /// 流式捕获执行：与 <see cref="RunCaptureAsync"/> 同返回形态（exit/stdout/stderr 在截断后累积，
    /// 超长行按 <see cref="MaxStreamLineChars"/> 截断——长错误行进失败文案时已被截断，定位够用），
    /// 另把子进程每行输出经 <paramref name="log"/> 透传（`[bootstrap] &lt;exe&gt;&gt;` 前缀，外部输出原文直放，
    /// 对齐 stderr 尾巴惯例）。行泵与整树击杀复用 <see cref="Plugins.PluginProcessRunner"/> 同语义。
    /// </summary>
    internal static async Task<(int Exit, string Stdout, string Stderr)> RunStreamingCaptureAsync(
        Action<string> log, string exe, IReadOnlyList<string> args, bool english, CancellationToken ct)
    {
        ProcessStartInfo psi = BuildCapturePsi(exe, args);
        string tag = Path.GetFileNameWithoutExtension(StripExtendedPrefix(exe));

        log?.Invoke($"[bootstrap] run: {psi.FileName} {string.Join(' ', args)}");
        using Process p = Process.Start(psi)
            ?? throw new InvalidOperationException(UiCopy.BootstrapProcessStartFailed(exe, english));
        // stdin 已重定向即关闭写端：子进程读到 EOF 而非阻塞等宿主（见 BuildCapturePsi）。
        p.StandardInput.Close();
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        try
        {
            // 双流并发泵：与 RunCaptureAsync 同一死锁防御（单流先读满会互等）
            Task[] pumps = new[]
            {
                Plugins.PluginProcessRunner.PumpAsync(p.StandardOutput, outSb, line => log?.Invoke($"[bootstrap] {tag}> {line}"), ct, MaxStreamLineChars),
                Plugins.PluginProcessRunner.PumpAsync(p.StandardError, errSb, line => log?.Invoke($"[bootstrap] {tag}> {line}"), ct, MaxStreamLineChars),
            };
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(pumps).ConfigureAwait(false);
            return (p.ExitCode, outSb.ToString(), errSb.ToString());
        }
        catch (Exception)
        {
            // 取消/异常路径必须整树击杀（与 RunCaptureAsync 同一孤儿防御）
            Plugins.PluginProcessRunner.KillTree(p);
            throw;
        }
    }

    /// <summary>
    /// 解析装机 node 的 npm 全局 bin 目录（P1 根因修复）：`npm config get prefix` 本地查询（无网络），
    /// 目录映射复用 <see cref="NodeBinDir"/>（R1 S4，不手抄第二份）。目录不存在或查询失败返回 null
    /// （调用方沿用既有 PATH）。纯定位逻辑可单测（hooks 注入）。
    /// </summary>
    internal static async Task<string?> ResolveNpmGlobalBinDirAsync(
        NodeResult node, RuntimeBootstrapHooks hooks, CancellationToken ct)
    {
        try
        {
            (int exit, string? stdout, string? _) = await hooks.RunProcessAsync(
                node.NodePath, [node.NpmCli, "config", "get", "prefix"], ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return null;
            }

            string prefix = (stdout ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return null;
            }

            string binDir = NodeBinDir(prefix);
            return Directory.Exists(binDir) ? binDir : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best-effort 降级（R2 S3）：前缀查询异常一律沿用既有 PATH（VerifyDsh 失败会给出指引，
            // 不静默装坏）；OCE 不吞，调用链收口。ex 仅用于过滤器区分 OCE。
            return null;
        }
    }

    /// <summary>验证全局 dsh 版本可解析：先统一启动命令直解（Unix PATH 裸名 / Windows cmd 中转），
    /// 失败（退出码非零或起不来抛错）回退 npm 全局 bin 垫片直验（Windows 复用预装 node 的 P1 形态：
    /// npm 前缀 bin 未进进程 PATH 时裸名在 CreateProcess 下不可解析，而垫片文件真实存在）。
    /// spawn 异常一律按验证失败处理进回退，不抛新异常面（run 36310235841 的 09:50:14 Win32Exception 实证）。</summary>
    internal static async Task<string?> VerifyDshAsync(
        RuntimeBootstrapOptions options, NodeResult node, Action<BootstrapProgress> report, RuntimeBootstrapHooks hooks, bool english, CancellationToken ct)
    {
        DshCommand primary = DshCommandFor(null, ["--version"]);
        try
        {
            (int exit, string? stdout, string? _) = await WithStepTimeoutAsync(options.StepTimeoutMinutes, english, ct,
                token => hooks.RunProcessAsync(primary.Exe, primary.Args, token)).ConfigureAwait(false);
            if (exit == 0 && RuntimeVersionGate.TryParseVersionOutput(stdout ?? string.Empty) is { } v)
            {
                return v;
            }

            report(new BootstrapProgress(BootstrapStep.VerifyDsh, "PATH 未命中 dsh，回退 npm 全局 bin 垫片直验"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            report(new BootstrapProgress(BootstrapStep.VerifyDsh, $"PATH 直解失败（{ex.GetType().Name}），回退 npm 全局 bin 垫片直验"));
        }

        return await TryVerifyDshViaNpmBinShimAsync(options, node, report, hooks, english, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// npm 全局 bin 垫片直验（VerifyDsh 的 PATH 失败回退，Windows 复用预装 node 的 P1 形态）：
    /// 经 <see cref="DshCommandFor"/> 跑垫片（Windows 走 cmd 中转，Unix 直跑），文件存在性先行，
    /// 不猜测执行；可跑则把 bin 目录补进进程 PATH（后续子进程可直解）并返回版本，否则返回 null。
    /// Spawn 自身抛错（缺文件/不可执行）同样返回 null——调用方统一走既有指引失败，不抛新异常面。
    /// </summary>
    internal static async Task<string?> TryVerifyDshViaNpmBinShimAsync(
        RuntimeBootstrapOptions options, NodeResult node, Action<BootstrapProgress> report, RuntimeBootstrapHooks hooks, bool english, CancellationToken ct)
    {
        string? binDir = await ResolveNpmGlobalBinDirAsync(node, hooks, ct).ConfigureAwait(false);
        string shim = DshShimPath(binDir);
        if (binDir is null || !File.Exists(shim))
        {
            return null;
        }

        DshCommand cmd = DshCommandFor(binDir, ["--version"]);
        try
        {
            (int shimExit, string? shimStdout, string? _) = await WithStepTimeoutAsync(options.StepTimeoutMinutes, english, ct,
                token => hooks.RunProcessAsync(cmd.Exe, cmd.Args, token)).ConfigureAwait(false);
            string? shimVersion = RuntimeVersionGate.TryParseVersionOutput(shimStdout ?? string.Empty);
            if (shimExit != 0 || shimVersion is null)
            {
                return null;
            }

            PrependPathToProcessEnv(binDir);
            report(new BootstrapProgress(BootstrapStep.VerifyDsh, $"垫片直验通过并暴露到 PATH：{binDir}"));
            return shimVersion;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 垫片不可 spawn（删档竞速/权限）：按验证失败处理，走既有指引，不抛新异常面。
            return null;
        }
    }

    /// <summary>探测 PATH 上系统全局 node（取真实可执行路径）+ 其 npm-cli.js。全局 node 是那份唯一 dsh 的运行时，
    /// 桌面与终端共用（ADR simple-shell-single-global-dsh）。</summary>
    private static async Task<(string? NodePath, string? NpmCli)> ProbeLocalNodeAsync(Action<string> log, bool english, CancellationToken ct)
    {
        try
        {
            // 确认 node 可执行（--version 非零即视为不存在），并取真实路径（process.execPath），
            // npm-cli 紧邻 node 安装在发行包布局内。
            (int exit, string? stdout, string _) = await RunCaptureAsync(log, "node", ["-e", "console.log(process.execPath)"], english, ct).ConfigureAwait(false);
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

    /// <summary>npm 安装步内存活自报间隔（秒）：npm 自身输出不可靠（CI 非 TTY 下可全程静默），
    /// 引导侧按此节拍自报一行，与 npm 输出无关；npm 返回即停。</summary>
    internal const int NpmAliveReportSeconds = 60;

    /// <summary>npm 安装步内存活循环：每 <paramref name="intervalSeconds"/> 报一行进度（InstallDsh 步），
    /// 调用方在 npm 返回后取消 <paramref name="ct"/> 并等待本循环收尾。取消即静默返回，无其他副作用。</summary>
    internal static async Task NpmAliveLoopAsync(Action<BootstrapProgress> report, int intervalSeconds, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            int elapsed = 0;
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                elapsed += intervalSeconds;
                report(new BootstrapProgress(BootstrapStep.InstallDsh, $"npm 安装进行中（已 {elapsed}s）…"));
            }
        }
        catch (OperationCanceledException)
        {
            // 调用方取消（npm 返回或应用退出）：静默收尾。
        }
    }

    /// <summary>步骤级超时包装：应用退出（appCt）取消仍以 OCE 上抛；仅步超时转异常走失败重试页。</summary>
    private static Task WithStepTimeoutAsync(int minutes, bool english, CancellationToken appCt, Func<CancellationToken, Task> action) =>
        WithStepTimeoutAsync<object?>(minutes, english, appCt, async token =>
        {
            await action(token).ConfigureAwait(false);
            return null;
        });

    private static async Task<T> WithStepTimeoutAsync<T>(int minutes, bool english, CancellationToken appCt, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        cts.CancelAfter(TimeSpan.FromMinutes(minutes));
        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!appCt.IsCancellationRequested)
        {
            throw new InvalidOperationException(UiCopy.BootstrapStepTimeout(minutes, english));
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
