using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// <see cref="StartupSequence"/> 的监督与退出接线面：监督器装配（引导落定门控）、退出管道、
/// 恢复屏、收养导航、页面健康观测与共享 home 横幅。启动主链在 <c>StartupSequence.cs</c>。
/// </summary>
internal sealed partial class StartupSequence
{
    private SupervisorSetup SetupSupervisor(HostSetup host)
    {
        // 原 `using var supervisorCts`：生命周期由 Run 的 finally 释放（本方法接线 wiring.SupervisorCts）。
        var cts = new CancellationTokenSource();
        _wiring.SupervisorCts = cts; // 自更新后台任务 token 持有器接线（见 StartupWiring）
        RynNavigationCallbacks navCallbacks = _app.App.Services.GetRequiredService<RynNavigationCallbacks>();
        var supervisor = new RuntimeSupervisor(
            host.Host,
            restartTimeout: TimeSpan.FromSeconds(_timeouts.SupervisorRestartTimeoutSeconds),
            recoveredRetryDelay: TimeSpan.FromSeconds(_timeouts.SupervisorRecoveredRetryDelaySeconds),
            failedRetryDelay: TimeSpan.FromSeconds(_timeouts.SupervisorFailedRetryDelaySeconds),
            showRecovery: isLockBlocked => ShowRecoveryPageAsync(host, isLockBlocked),
            navigate: url => NavigateAfterAdoptAsync(navCallbacks, url),
            log: HostLog.Write);
        // 引导期门控：宿主尚无 dsh 进程时 WaitForExitAsync 立即完成，监督器会空转进恢复循环
        // 并用恢复屏覆写引导页——必须等引导落定（成功 spawn 或确认放弃）才进入监视。
        var supervisorTask = Task.Run(async () =>
        {
            // 引导落定握手（理由见上方「引导期门控」注释；网 = BootstrapSettleGateTests）。
            if (!await _preflight.Bootstrap.WaitSettledAsync(timeout: null, cts.Token))
            {
                return;
            }

            await supervisor.RunAsync(cts.Token);
        });

        // 有序退出编排 + 自更新兜底收割器接线（WireExitHandlers）
        WireExitHandlers(_app.App.Services.GetRequiredService<IRynWindow>(), host, cts);

        return new SupervisorSetup(cts, supervisorTask);
    }

    /// <summary>单实例退出管道接线（ADR composition-root-value-flow-pipeline）：有序退出步骤即构造数据，
    /// 托盘有序退出与 Run 尾部共用同一实例，幂等由 once-guard 保证；运行时回收先于关窗。</summary>
    private void WireExitHandlers(IRynWindow quitWindow, HostSetup host, CancellationTokenSource supervisorCts)
    {
        _wiring.Exit = new ExitPipeline(
            supervisorCts.Cancel,
            host.Host.Stop,
            () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), host.Marker.Token),
            () => _instanceListener?.Dispose(),
            quitWindow.Close,
            log: HostLog.Write);
    }

    /// <summary>恢复屏展示 + 恢复周期起点打点（ADR adopt-skip-navigate-on-self-reload）。</summary>
    /// <param name="isLockBlocked">残留锁死跳过重启（ADR residue-lock-fail-loud）：原因取锁文案（恢复页提示），否则普通崩溃原因。</param>
    private ValueTask ShowRecoveryPageAsync(HostSetup host, bool isLockBlocked = false)
    {
        // 周期起点：子进程退出后、RestartAsync 等待前。周期内的导航到达即页内自刷
        // （市场 doRestart 轮询到新 boot 即 reload），收养 navigate 据此免导航。
        _wiring.LastRecoveryShownAtUtc = DateTimeOffset.UtcNow;
        // 恢复页三件套（ADR diag-masking-and-recovery-page）：失败原因 + stderr 尾部展示 +
        // 导出诊断/退出动作。desktop.* 走 Ryn 层 IPC 不依赖 dsh 存活；数据经 textContent
        // 回填（stderr 是上游不可控输出，绝不 innerHTML 拼接）
        var tail = host.Host.StderrTail.TakeLast(12).ToList();
        string reason = isLockBlocked
            ? UiCopy.ReasonDshResidueLocked(_uiLocale.IsEnglish)
            : UiCopy.ReasonRuntimeCrashed(_uiLocale.IsEnglish);
        _ = _app.WindowAccessor.Current.EvaluateJavaScriptAsync(
            RecoveryPageBuilder.BuildScript(reason, tail, _uiLocale.IsEnglish));
        return ValueTask.CompletedTask;
    }

    /// <summary>收养后导航：epoch 可能已换（新 secret/端口）→ 先重铸（覆盖式，每次全量 HTTP），再定导航。
    /// 页内已自刷即免导航，只做收养登记（导航靶点恒为代理根）。</summary>
    private ValueTask NavigateAfterAdoptAsync(RynNavigationCallbacks navCallbacks, Uri url)
    {
        // webUrl 恒代理根（导航靶点与健康 reload 靶点）；收养登记只刷新铸币态，dsh 旧 URL 不再进导航。
        // 页面永驻代理内：Ryn dev-server 分支已自动信任代理源，无需逐跳授权；
        // dsh 自指 3xx 由代理内部跟完，页内绝对 dsh 链接走导航回调外部策略（fail-closed），外链照走系统浏览器。
        // 同步编排沿用既有形态：重铸内部超时兜底，无 ct 位（收养回调无取消语义）；失败 loud，导航照发
        // （转发 401/502 → 探针/恢复面按错误页处理）。
        _ = _shellForward.MintAsync(DshWebUrl.From(url), HostLog.Write, CancellationToken.None).GetAwaiter().GetResult();
        // 免导航（ADR adopt-skip-navigate-on-self-reload）：周期内有到达即视为页内自刷
        // （市场 doRestart 轮询到新 boot 即 location.reload；谓词只比时间戳，同源靠前提假设），
        // 再导航即多余——只做收养登记，跳过实际导航。无到达时走壳单跳。
        if (AdoptNavigateGate.ShouldSkipAdoptNavigate(navCallbacks.LastNavigatedAtUtc, _wiring.LastRecoveryShownAtUtc))
        {
            HostLog.Write($"[nav] 收养时恢复周期内已有页面到达（视为页内自刷，{url.GetLeftPart(UriPartial.Authority)}），跳过代理侧导航");
            return ValueTask.CompletedTask;
        }

        if (_proxy is null)
        {
            HostLog.Write("[nav] 收养导航无代理源（回环绑定失败），跳过本次导航");
            return ValueTask.CompletedTask;
        }

        return _app.WindowAccessor.Current.NavigateAsync(_proxy.Url);
    }

    private void SetupHealthMonitor(SupervisorSetup supervisor)
    {
        // 页面健康观测 + 有界恢复（ADR page-health-monitor / reference-alignment 批次五）：
        // 宿主只读探针轮询，不注入不依赖 companion——「dsh 在跑但页面空白」类事故（历史三起全靠
        // 人肉发现）从此有自动留痕；连续 Dead 达阈值后在预算内触发一次有界 reload，耗尽转观测-only，
        // 成功恢复复位预算（防误报引发无限重载循环，对齐参照 plugin_boot.rs 的有界刷新门控）。
        // 首拍延迟（HealthInitialDelaySeconds）避开启动空窗，探针异常按 Unknown 续跑。reload 委托
        // 恒为当前代理靶点（代理源恒定，未铸币时 holder 自 reload）；代理绑定失败时该窗口页面是
        // wwwroot 引导页（有内容 → Alive），不会进入 Dead 恢复分支——空态只是防御性兜底。
        _wiring.HealthMonitor = new PageHealthMonitor(
            _app.WindowAccessor,
            HostLog.Write,
            reload: ct => _proxy is null
                ? ValueTask.CompletedTask
                : _app.WindowAccessor.Current.NavigateAsync(_proxy.Url, ct));
        _ = _wiring.HealthMonitor.RunAsync(TimeSpan.FromSeconds(_timeouts.HealthInitialDelaySeconds), supervisor.Cts.Token);
    }

    private void SharedHomeBannerTask(HostSetup host, SupervisorSetup supervisor)
    {
        // 共享 home 切换的启动期告知（ADR shared-home-desktop-profile）：版本底线检查 + 旧 home 一次性提示。
        // 随包插件现于 spawn dsh 前安装（不再「启动后装 → 覆写页面并重启运行时」），横幅无需等安装收尾，
        // 只需等首启引导落定——版本探针走 PATH 上全局 dsh（bundled=null），提前跑会探到空。
        _ = Task.Run(async () =>
        {
            // 引导落定前横幅不抢跑；超时（BootstrapSettleTimeoutSeconds）按已定继续（降级语义在 BootstrapSettleGate 内），取消即放弃。
            if (!await _preflight.Bootstrap.WaitSettledAsync(TimeSpan.FromSeconds(_timeouts.BootstrapSettleTimeoutSeconds), supervisor.Cts.Token))
            {
                return;
            }

            string home = HarnessRuntimeHost.ResolveDshHome();
            string? detected = await RuntimeVersionGate.ProbeAsync(supervisor.Cts.Token);
            if (detected is not null)
            {
                HostLog.Write($"[host] dsh 版本 {detected}（底线 {RuntimeVersionGate.MinimumVersion}）");
                if (RuntimeVersionGate.IsBelowFloor(detected))
                {
                    HostLog.Write($"[host] 警告：dsh {detected} 低于支持底线 {RuntimeVersionGate.MinimumVersion}，已提示用户");
                    await PagePump.ShowBannerWhenReadyAsync(_app.WindowAccessor, DesktopBanner.BuildVersionFloorBanner(detected, _uiLocale), supervisor.Cts.Token);
                }
            }
            else
            {
                HostLog.Write("[host] dsh 版本探测失败，跳过底线检查");
            }

            // 旧 home 留痕仅进日志（界面横幅已按用户拍板去除，ADR companion-settings-consolidation）；
            // 指回旧目录时不记「改用新目录」——自相矛盾且无信息量
            if (LegacyHomeNotice.IsPresent() && !PathsEqual(home, LegacyHomeNotice.LegacyPrivateHome))
            {
                HostLog.Write($"[host] 检测到旧版桌面数据目录 {LegacyHomeNotice.LegacyPrivateHome}；新版使用 {home}（未迁移）");
            }

            // 上轮非受控退出：提示但不暗示应用故障（用户杀进程也属此类），引导导出诊断
            if (host.Marker.PreviousRunUnclean)
            {
                await PagePump.ShowBannerWhenReadyAsync(_app.WindowAccessor, DesktopBanner.BuildUncleanExitBanner(_uiLocale), supervisor.Cts.Token);
            }
        });
    }

    /// <summary>引导完成后的铸币收尾：覆盖式重铸（本方法是 bootstrap 路径的 epoch 起点）。
    /// 不导航（鉴权自愈的同源重载除外）——窗口恒在代理源上，未铸币时 holder 自轮询就绪后自 reload
    /// （renderer 发起，绕开 saucer set_url 原生挂家族；见 ADR loopback-forward-proxy）。
    /// 由引导服务在 dsh 就位时回调。</summary>
    /// <param name="url">dsh 就位端点（仅供落定重铸与日志；无需导航，恒驻代理源）。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task EnterMainUiAsync(DshWebUrl url, CancellationToken ct)
    {
        if (_proxy is null)
        {
            HostLog.Write("[nav] 进入主界面无代理源（回环绑定失败），本次不导航（dsh 已就绪，重启即进）");
            return;
        }

        // 壳铸币先行（不依赖窗口：纯 HTTP；holder 的就绪轮询以此次铸币为门——先铸才放行）。
        _ = await _shellForward.MintAsync(url, HostLog.Write, ct).ConfigureAwait(false);
        // 窗口可能尚未建好（原生建窗慢于 dsh 就位时，首个 Current 即抛，ADR bootstrap-window-ready-wait）：
        // 有界等可用，超时 loud 跳过本次（dsh 已就绪，holder 自 reload 即进）。
        if (!await WaitForWindowAsync(ct).ConfigureAwait(false))
        {
            HostLog.Write($"[nav] 等窗口可用超时（{_timeouts.WindowReadyTimeoutSeconds}s），跳过本次（holder 自 reload 即进）");
            return;
        }

        await SettleWebSessionAsync(url, ct);
    }

    /// <summary>路径等值判定（Windows 不区分大小写）——旧 home 提示的指回守卫用。</summary>
    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
