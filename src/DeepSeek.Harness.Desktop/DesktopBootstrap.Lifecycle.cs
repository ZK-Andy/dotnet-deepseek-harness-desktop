using DeepSeek.Harness.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的生命周期编排面（partial，ADR 尺寸健康闸）：更新栈装载、
/// 监督器/健康监视器/引导/更新检查/共享 home 告知。组合根只装配——编排集中于此，应用装配
/// 见 <c>DesktopBootstrap.App.cs</c>，核心入口在 <c>DesktopBootstrap.cs</c>。
/// </summary>
public sealed partial class DesktopBootstrap
{
    private UpdateToken InitCloseGateAndUpdateStack(RuntimeToken runtime)
    {
        // hide-to-tray 关窗闸门（ADR shell-tray-hide-to-tray）：托盘「退出」与自更新安装路径
        // 先批准再 Close。用户普通关窗是否转隐藏由 closeBehavior 偏好裁决（默认 true 保持
        // 历史行为）；托盘未就绪时拦截不生效（关窗直退）。
        var closeGate = new Services.Tray.CloseGate();
        var closeBehavior = new CloseBehaviorPreference(
            Path.Combine(HarnessRuntimeHost.ResolveDshHome(), CloseBehaviorPreference.FileName));

        // 自更新协调器在此构造并装载（早于 BuildApp）：状态机装载/就绪横幅/后台检查从组合根下沉，
        // HttpClient 构造随协调器迁出组合根（ADR composition-root-value-flow-pipeline 批次 1）。
        _updates = new Services.Update.UpdateCoordinator(
            _isDev,
            () => _windowAccessor,
            closeGate,
            _uiLocale,
            () => _supervisorCtsRef?.Token ?? CancellationToken.None,
            ct => _exit.ScheduleExitFallback(ct),
            HostLog.Write);
        _updates.Load();

        // 托盘控制器在此构造（早于 BuildApp/ShowTray），窗口与 Ryn 服务以惰性委托注入——
        // 控制器持有 hide-to-tray 拦截、唤回采样、菜单重建与关窗闸门/偏好（供路由构造注入）。
        _tray = new Services.Tray.TrayController(
            () => _app.Services.GetRequiredService<IRynWindow>(),
            () => _windowAccessor,
            closeGate,
            closeBehavior,
            _uiLocale,
            _updates.Machine,
            HostLog.Write);

        // token：InitCloseGateAndUpdateStack 完成（关窗闸门/closeBehavior/自更新协调器/托盘控制器已就绪）。
        return default;
    }

    private void RunBootstrapIfNeeded(AppToken app)
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），引导服务后台完成
        // 检测/下载/安装/验证后起 dsh，并把就位 URL 交回调接回壳侧导航；失败推错误态等待用户重试
        // （desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控——依赖序即插入位。
        _bootstrap.Start(EnterMainUiAsync);
    }

    private SupervisorToken SetupSupervisor(AppToken app, HostToken host)
    {
        // 原 `using var supervisorCts`：生命周期由 Run 的 finally 释放（本方法赋值）。
        _supervisorCts = new CancellationTokenSource();
        _supervisorCtsRef = _supervisorCts; // 自更新后台任务 token 持有器接线（见顶部声明）
        _supervisor = new RuntimeSupervisor(
            _host,
            restartTimeout: TimeSpan.FromSeconds(60),
            showRecovery: () =>
            {
                // 恢复页三件套（ADR diag-masking-and-recovery-page）：失败原因 + stderr 尾部展示 +
                // 导出诊断/退出动作。desktop.* 走 Ryn 层 IPC 不依赖 dsh 存活；数据经 textContent
                // 回填（stderr 是上游不可控输出，绝不 innerHTML 拼接）
                var tail = _host.StderrTail.TakeLast(12).ToList();
                _ = _windowAccessor.Current.EvaluateJavaScriptAsync(
                    Services.RecoveryPageBuilder.BuildScript(UiCopy.ReasonRuntimeCrashed(english: false), tail));
                return ValueTask.CompletedTask;
            },
            // 崩溃恢复导航同步刷新 webUrl——健康监视器（有界恢复）靠它作为 reload 靶点；若
            // 崩溃重启用新端口（ADDR child-process-reaping-port-drift 的端口漂移）而 webUrl
            // 仍指向旧 URL，监视器的 reload 会打到已死的旧端口、甚至覆写刚恢复的导航。
            navigate: url =>
            {
                _webUrl = url;
                AuthorizeIpcOriginFor(url);
                return _windowAccessor.Current.NavigateAsync(url);
            },
            log: HostLog.Write);
        // 引导期门控：宿主尚无 dsh 进程时 WaitForExitAsync 立即完成，监督器会空转进恢复循环
        // 并用恢复屏覆写引导页——必须等引导落定（成功 spawn 或确认放弃）才进入监视。
        _supervisorTask = Task.Run(async () =>
        {
            // 引导落定握手（理由见上方「引导期门控」注释；网 = BootstrapSettleGateTests）。
            if (!await _bootstrap.WaitSettledAsync(timeout: null, _supervisorCts.Token))
            {
                return;
            }

            await _supervisor.RunAsync(_supervisorCts.Token);
        });

        // 有序退出编排 + 自更新兜底收割器接线（WireExitHandlers）
        WireExitHandlers(_app.Services.GetRequiredService<IRynWindow>());

        // token：SetupSupervisor 完成（supervisorCts/监督器/退出编排已接线），供后续阶段按类型承诺串联。
        return default;
    }

    /// <summary>单实例退出管道接线（ADR composition-root-value-flow-pipeline）：有序退出步骤即构造数据，
    /// 托盘有序退出与 Run 尾部共用同一实例，幂等由 once-guard 保证；运行时回收先于关窗。</summary>
    private void WireExitHandlers(IRynWindow quitWindow)
    {
        _exit = new Core.ExitPipeline(
            () => _supervisorCts.Cancel(),
            _host.Stop,
            () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), _marker.Token),
            () => _instanceListener?.Dispose(),
            quitWindow.Close,
            log: HostLog.Write);
    }

    private void SetupHealthMonitor(SupervisorToken supervisor)
    {
        // 页面健康观测 + 有界恢复（ADR page-health-monitor / reference-alignment 批次五）：
        // 宿主只读探针轮询，不注入不依赖 companion——「dsh 在跑但页面空白」类事故（历史三起全靠
        // 人肉发现）从此有自动留痕；连续 Dead 达阈值后在预算内触发一次有界 reload，耗尽转观测-only，
        // 成功恢复复位预算（防误报引发无限重载循环，对齐参照 plugin_boot.rs 的有界刷新门控）。
        // 首拍延迟 10s 避开启动空窗，探针异常按 Unknown 续跑。reload 委托捕获 webUrl（字段，
        // 初始/引导完成/崩溃恢复导航三处都会刷新，见上文与 RuntimeSupervisor 的 navigate），
        // 恒为当前 dsh web 靶点；webUrl 只有当引导未落定（dsh 未起）才为空，而该窗口页面是
        // wwwroot 引导页（有内容 → Alive），不会进入 Dead 恢复分支——reload 委托的空态只是防御性兜底。
        _healthMonitor = new Services.PageHealthMonitor(
            _windowAccessor,
            HostLog.Write,
            reload: ct => _webUrl is null
                ? ValueTask.CompletedTask
                : _windowAccessor.Current.NavigateAsync(_webUrl, ct));
        _ = _healthMonitor.RunAsync(TimeSpan.FromSeconds(10), _supervisorCts.Token);
    }

    private void StartUpdateCheck(SupervisorToken supervisor)
    {
        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        _updates.Start();
    }

    private void SharedHomeBannerTask(SupervisorToken supervisor)
    {
        // 共享 home 切换的启动期告知（ADR shared-home-desktop-profile）：版本底线检查 + 旧 home 一次性提示。
        // 随包插件现于 spawn dsh 前安装（不再「启动后装 → 覆写页面并重启运行时」），横幅无需等安装收尾，
        // 只需等首启引导落定——版本探针走 PATH 上全局 dsh（bundled=null），提前跑会探到空。
        _ = Task.Run(async () =>
        {
            // 引导落定前横幅不抢跑；120s 超时按已定继续（降级语义在 BootstrapSettleGate 内），取消即放弃。
            if (!await _bootstrap.WaitSettledAsync(TimeSpan.FromSeconds(120), _supervisorCts.Token))
            {
                return;
            }

            string home = HarnessRuntimeHost.ResolveDshHome();
            string? detected = await RuntimeVersionGate.ProbeAsync(_supervisorCts.Token);
            if (detected is not null)
            {
                HostLog.Write($"[host] dsh 版本 {detected}（底线 {RuntimeVersionGate.MinimumVersion}）");
                if (RuntimeVersionGate.IsBelowFloor(detected))
                {
                    HostLog.Write($"[host] 警告：dsh {detected} 低于支持底线 {RuntimeVersionGate.MinimumVersion}，已提示用户");
                    await Services.PagePump.ShowBannerWhenReadyAsync(_windowAccessor, Services.DesktopBanner.BuildVersionFloorBanner(detected, _uiLocale), _supervisorCts.Token);
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
            if (_marker.PreviousRunUnclean)
            {
                await Services.PagePump.ShowBannerWhenReadyAsync(_windowAccessor, DesktopBanner.BuildUncleanExitBanner(_uiLocale), _supervisorCts.Token);
            }
        });
    }
}
