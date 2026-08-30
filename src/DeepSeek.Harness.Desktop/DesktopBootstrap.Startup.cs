using System.Runtime.InteropServices;
using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Callbacks;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// DesktopBootstrap 辅助方法（partial，ADR split-program-main-god-function）：
/// 原 Main 的局部函数/静态辅助 → 私有方法；静态辅助转 private static，有状态局部函数转实例方法访问字段。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>8s 退出看门狗（无令牌，退出即终态）：主循环届时仍未返回则强制终结。Exit 不展开栈，
    /// 已显式完成的 Cancel/Stop/Release/unlink 不会被二次执行，无双重释放面；
    /// Exit 前再补一次 Stop（幂等）——封 supervisor 恢复分支 spawn-after-cancel 竞态下
    /// 刚被拉起的子进程（StartCoreAsync 的取消检查点之外的残余窗口）。</summary>
    private void StartQuitWatchdog()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            Services.HostLog.Write("[tray] 退出看门狗触发：主循环未返回，强制结束");
            _host.Stop();
            Environment.Exit(0);
        });
    }

    /// <summary>安装授权通过后的兜底退出：窗口 Close 未生效时强制结束进程，放行安装脚本。
    /// 改掉裸 <c>Environment.Exit(0)</c>——GTK 主循环滞留时它会绕过 Run 尾部的 <c>host.Stop()</c>，
    /// dsh 子进程成孤儿占住首选端口（ADR self-update-exit-reaps-dsh-child，v0.3.11 实机复现）。
    /// 兜底强退前先经 <see cref="_updateExitReaper"/> 确定性收割（cancel 监督器 + 整树击杀 dsh + 释放 marker），
    /// reaper 未接线时回退直杀 dsh，仍不泄漏。与托盘有序退出编排同款三件套，<c>host.Stop()</c> 幂等无双重收割面。</summary>
    private void StartExitFallback(CancellationToken ct)
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Services.HostLog.Write("[update] 退出兜底触发：回收 dsh 后强退");
            if (_updateExitReaper is not null)
            {
                _updateExitReaper();
            }
            else
            {
                _host.Stop();
            }

            Environment.Exit(0);
        });
    }

    /// <summary>hide-to-tray：先采样窗口态留证，再把窗口藏起来而非销毁。失败只留日志，不拖垮关窗链路。</summary>
    private async Task HideForTrayAsync(IRynWindow window)
    {
        try
        {
            // 原生查询 IRynWindow.IsMaximized（Ryn 0.30.3 起暴露，本仓自 0.30.4 消费）
            Volatile.Write(ref _maximizedAtHide, window.IsMaximized ? 1 : 0);
            // 隐藏即采样的留痕+主线程活性证据：唤回行为异常的排查需要知道「隐藏时看到什么」
            Services.HostLog.Write($"[tray] 窗口隐藏到托盘（隐藏前最大化采样={Volatile.Read(ref _maximizedAtHide)}）");
        }
        catch (Exception ex)
        {
            // deferred 代理在窗口未就绪时可能抛出：按未知处理，唤回路径对未知不动作
            Volatile.Write(ref _maximizedAtHide, -1);
            Services.HostLog.Write($"[tray] 最大化采样失败：{ex.Message}");
        }

        try
        {
            await window.HideAsync();
            Services.HostLog.Write("[tray] 窗口已隐藏");
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[tray] 隐藏窗口失败：{ex.Message}");
        }
    }

    /// <summary>托盘唤回（原 RecallAsync 局部函数）：样本入口即取本地快照，finally 无条件消费——
    /// 若下方任一 await 抛出，残留样本会让下一次托盘点击把用户手动还原的窗口误最大化。
    /// Linux 隐藏态预置 + 显示后兜底确认两拍（ADR tray-recall-maximize-and-check-feedback）。</summary>
    private async Task RecallAsync(IRynWindow trayWindow)
    {
        int sample = Volatile.Read(ref _maximizedAtHide);
        try
        {
            // Linux 隐藏态预置（v0.3.6 实机反馈的首唤闪变在此消除）：对未映射窗口
            // 显式设最大化——幂等、不读事件镜像（该机实证镜像不可信，旧镜像门控
            // 让预置永不触发=闪变复发）。GTK 把未映射窗口的 maximize 记为初始态，
            // map 时直接以最大化呈现。deferred 代理在窗口未就绪时可能抛出：放弃
            // 预置，兜底确认仍在。
            try
            {
                if (OperatingSystem.IsLinux() && Services.Tray.TrayRecallMaximize.ShouldEnsure(sample))
                {
                    trayWindow.SetMaximized(true);
                    Services.HostLog.Write("[tray] 唤回：隐藏态已预置最大化");
                }
            }
            catch (Exception ex)
            {
                Services.HostLog.Write($"[tray] 隐藏态预置最大化失败：{ex.Message}");
            }

            await trayWindow.ShowAsync().AsTask();
            // 兜底确认（各平台统一的第二拍）：显示后显式设最大化。目标态幂等
            // ——预置已生效时这里是原生层 no-op，无需任何跳过守卫；延迟一拍沿用
            // 既有节奏（亚秒级等待不接监督器取消令牌）。
            if (Services.Tray.TrayRecallMaximize.ShouldEnsure(sample))
            {
                await Task.Delay(300);
                trayWindow.SetMaximized(true);
                // 兜底拍留痕：预置拍已打日志，此拍若不落痕，「两拍只走了一拍」无从判别
                Services.HostLog.Write("[tray] 唤回：显示后兜底确认已发");
            }
        }
        finally
        {
            Volatile.Write(ref _maximizedAtHide, -1);
        }
    }

    /// <summary>路径等值判定（Windows 不区分大小写）——旧 home 提示的指回守卫用。</summary>
    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// CLI shim 注册（ADR reference-alignment 批次四）：运行时就位后把 dsh/pnpm 注册进用户 PATH。
    /// best-effort——registrar 已吞预期异常；此处再兜底任何意外异常（注册是增强信息，绝不阻断启动）。
    /// dev 隔离时跳过 dsh shim（防把开发环境烘焙进共享 shim），只写内容恒定的 pnpm shim。
    /// </summary>
    private void RegisterCliShim(bool devIsolated)
    {
        try
        {
            if (RuntimeLocator.TryLocateRuntimeDirectory() is { } runtimeDir)
            {
                new Services.CliShimRegistrar(Services.HostLog.Write).TryRegister(
                    runtimeDir, HarnessRuntimeHost.ResolveDshHome(), devIsolated);
            }
        }
        catch (Exception ex)
        {
            // 注册是增强信息：任何未预期异常都不该打断启动链路
            Services.HostLog.Write($"[cli-shim] 注册跳过（意外异常）：{ex.Message}");
        }
    }

    /// <summary>当前平台的更新资产 RID（与 release 资产命名后缀对应）。</summary>
    private static string UpdateRid()
    {
        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }

        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        return "unknown";
    }

    // TODO(retry-push-fold): PushBootstrapStateAsync 与 RetryPushPreinstallAsync 是 15×400ms 重试推送的
    // 逐字节同构（仅日志前缀与脚本不同），可折叠为一个 PushEventToPageWithRetryAsync(accessor, script, logTag)。
    /// <summary>
    /// 引导重试循环：单次尝试（RuntimeBootstrap.RunAsync）→ 成功返回运行时；失败推错误态并等待
    /// 重试信号（desktop.bootstrap.retry 经 <paramref name="gate"/> 放行）或应用退出
    /// （<paramref name="ct"/> 取消）。返回 null = 放弃（退出/取消）。
    /// </summary>
    private async Task<(string NodeExe, string DshEntry)?> RunBootstrapWithRetryAsync(
        Services.RuntimeBootstrapGate gate, CancellationToken ct)
    {
        var options = Services.RuntimeBootstrapOptions.Load(AppContext.BaseDirectory);
        string runtimeDir = RuntimeLocator.ResolveDownloadedRuntimeDirectory();
        RuntimeBootstrapHooks hooks = Services.RuntimeBootstrap.CreateDefaultHooks(Services.HostLog.Write);
        Services.HostLog.Write($"[bootstrap] 引导开始：runtimeDir={runtimeDir} dshSpec={options.DshSpec} node=v{options.NodeVersion}");

        while (true)
        {
            gate.Reset();
            BootstrapOutcome outcome;
            try
            {
                outcome = await Services.RuntimeBootstrap.RunAsync(
                    options,
                    runtimeDir,
                    progress => _ = Services.PagePump.PushBootstrapStateAsync(_windowAccessor, progress.Step.ToString(), progress.Message, progress.Failed),
                    hooks,
                    ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (outcome.Success && outcome.Runtime is { } runtime)
            {
                return runtime;
            }

            string reason = outcome.Error ?? "未知错误";
            Services.HostLog.Write($"[bootstrap] 引导失败：{reason}（等待用户重试或退出）");
            // 推实际失败步骤：进度页据此红色高亮失败环节（推 "Ready" 会让高亮不可达）
            await Services.PagePump.PushBootstrapStateAsync(_windowAccessor, outcome.Step.ToString(), reason, failed: true);

            // 等重试信号或应用退出；信号与取消都是即时语义事件，200ms 轮询足够
            while (!gate.IsSignaled && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }

            if (ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>流式执行器：把 <c>dsh plugin add</c> 的每行输出推给插件引导页日志区。
    /// 双执行器已折叠（原 TODO(executor-fold-killtree)）：统一走
    /// <see cref="Services.PluginProcessRunner.RunStreamingAsync"/>——单一实现、含取消/异常整树击杀
    /// （对齐 <c>RuntimeBootstrap.RunCaptureAsync</c> 防御不变量），引导路径传 bootCt 取消时
    /// 不再让 dsh plugin add 带 profile 写权成孤儿。</summary>
    private async Task<(int Exit, string Out, string Err)> RunDshPluginAddStreamingAsync(
        System.Diagnostics.ProcessStartInfo psi, CancellationToken ct)
    {
        return await Services.PluginProcessRunner.RunStreamingAsync(psi, ct, line => Services.PagePump.PushPreinstallLog(_windowAccessor, line));
    }

    /// <summary>
    /// 首启插件引导相（ADR reference-alignment 批次二）：运行时就位后（BindRuntime 后）、StartAsync 前，
    /// 若存在待装可选插件（preset），引导页呈现 chip + 确认/跳过 + 日志回流；用户确认才安装，跳过则不装。
    /// 5 分钟无决策默认跳过（避免壳永久挂在安装前、dsh 永不启动；跳过可经应用内市场补装）。
    /// </summary>
    private async Task RunPreinstallPhaseAsync(
        (string NodeExe, string DshEntry) runtime,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<(int Exit, string Out, string Err)>> runPluginAddStreaming,
        CancellationToken ct)
    {
        string home = HarnessRuntimeHost.ResolveDshHome();
        string profileDir = Path.Combine(home, "profiles", Services.HarnessRuntimeHost.DesktopProfileName);
        string profilePkg = Path.Combine(profileDir, "package.json");
        List<string> pending = Services.PresetPluginCatalog.PendingForFirstBoot(profilePkg, Services.HostLog.Write);
        if (pending.Count == 0)
        {
            Services.HostLog.Write("[host] 插件引导：无可选插件待装，跳过");
            return;
        }

        _preinstallGate.Reset();
        // 步骤高亮：引导页把「插件准备」步点亮（renderBootstrap 按 step 序置 active）。
        // 步骤名经枚举派生（单一事实源），避免与 JS STEP_ORDER 漂移。
        await Services.PagePump.PushBootstrapStateAsync(_windowAccessor, Services.BootstrapStep.PreinstallPlugins.ToString(), "可选插件准备", failed: false);
        await Services.PagePump.RetryPushPreinstallAsync(_windowAccessor, new Services.PreinstallFrame("decision", Plugins: pending.ToArray()));
        Services.HostLog.Write($"[host] 插件引导：呈现可选插件 {string.Join(", ", pending)}，等待用户决策（5 分钟超时默认跳过）");

        PreinstallChoice choice;
        try
        {
            choice = await _preinstallGate.Choice.WaitAsync(TimeSpan.FromMinutes(5), ct);
        }
        catch (TimeoutException)
        {
            Services.HostLog.Write("[host] 插件引导等待用户决策超时（5 分钟），默认跳过（可从应用内市场补装）");
            choice = PreinstallChoice.Skip;
        }

        if (choice == PreinstallChoice.Skip)
        {
            Services.HostLog.Write("[host] 插件引导：用户跳过，本次不安装可选插件");
            await Services.PagePump.RetryPushPreinstallAsync(_windowAccessor, new Services.PreinstallFrame("done", Action: "skip", Message: "已跳过插件安装"));
            await Services.PagePump.PushBootstrapStateAsync(_windowAccessor, Services.BootstrapStep.Ready.ToString(), "插件准备完成", failed: false);
            return;
        }

        Services.HostLog.Write("[host] 插件引导：用户确认，开始安装可选插件");
        try
        {
            await Services.PagePump.RetryPushPreinstallAsync(_windowAccessor, new Services.PreinstallFrame("installing", Plugin: Services.PresetPluginCatalog.Market));
            await Services.MarketInstallHelper.EnsureMarketFromRegistryAsync(
                runtime.NodeExe,
                runtime.DshEntry,
                home,
                Services.HostLog.Write,
                runPluginAddStreaming,
                ct);
            bool installed = Services.MarketInstallHelper.IsBundleInstalled(profilePkg, Services.PresetPluginCatalog.Market);
            await Services.PagePump.RetryPushPreinstallAsync(_windowAccessor, new Services.PreinstallFrame(
                "done", Action: "install", Ok: installed, Message: installed ? "安装完成" : "安装未成功（见日志）"));
            Services.HostLog.Write($"[host] 插件引导：可选插件安装{(installed ? "成功" : "未成功")}（{Services.PresetPluginCatalog.Market}）");
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[host] 插件安装异常：{ex.Message}");
            await Services.PagePump.RetryPushPreinstallAsync(_windowAccessor, new Services.PreinstallFrame("done", Action: "install", Ok: false, Message: ex.Message));
        }
        finally
        {
            // 步骤收尾：无论装/跳/失败，引导页把「插件准备」置 done 后再导航进主界面
            await Services.PagePump.PushBootstrapStateAsync(_windowAccessor, Services.BootstrapStep.Ready.ToString(), "插件准备完成", failed: false);
        }
    }
}
