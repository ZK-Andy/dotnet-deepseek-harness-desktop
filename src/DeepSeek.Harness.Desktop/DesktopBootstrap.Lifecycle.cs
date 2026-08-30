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
    private void InitCloseGateAndUpdateStack()
    {
        // hide-to-tray 关窗闸门（ADR shell-tray-hide-to-tray）：托盘「退出」与自更新安装路径
        // 先批准再 Close。用户普通关窗是否转隐藏由 closeBehavior 偏好裁决（默认 true 保持
        // 历史行为）；托盘未就绪时拦截不生效（关窗直退）。
        _closeGate = new Services.Tray.CloseGate();
        _closeBehavior = new Services.Tray.CloseBehaviorPreference(
            Path.Combine(HarnessRuntimeHost.ResolveDshHome(), Services.Tray.CloseBehaviorPreference.FileName));

        // 自更新栈（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）：
        // 状态机纯逻辑可单测，检查/下载/安装全部委托注入；状态经 CustomEvent 推给插件 UI。
        // dev 运行时不装载（除非 DSH_DESKTOP_UPDATE_FORCE=1 显式开启验证）：dev 构建版本同 csproj，
        // 一旦比对出新 release，点击会把官方包装进系统后按 Environment.ProcessPath 拉起**旧 dev 二进制**，
        // 版本不变、ready 记录不清，形成循环（审核加固，见 ADR self-update-review-hardening）。
        _updateEnabled = Services.Update.UpdateOptions.IsEnabledFor(
            _isDev,
            Environment.GetEnvironmentVariable(Services.Update.UpdateOptions.ForceDevEnv));
        _readyNotified = false;
        LoadUpdateMachine();
    }

    /// <summary>装载自更新状态机（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）。
    /// 检查/下载/安装全部委托注入；状态经 CustomEvent 推给插件 UI。dev 运行时不装载
    /// （除非 DSH_DESKTOP_UPDATE_FORCE=1 显式开启验证，避免 dev 版本循环）。</summary>
    private void LoadUpdateMachine()
    {
        _updateMachine = null;
        if (!_updateEnabled)
        {
            Services.HostLog.Write("[host] 自更新：dev 运行时不装载（DSH_DESKTOP_UPDATE_FORCE=1 可显式开启）");
            return;
        }

        var updateOptions = Services.Update.UpdateOptions.Load(AppContext.BaseDirectory);
        var updateHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // verify-code-conventions: ignore 组合根装配：自更新子域的 HttpClient 由组合根构造注入（属装配）
        string updatesDir = Path.Combine(HarnessRuntimeHost.ResolveDshHome(), updateOptions.UpdatesDirName);
        string? updatePkgKind = Services.Update.UpdatePlatform.DetectCurrentPackageKind();
        _updateMachine = new Services.Update.UpdateStateMachine(
            currentVersion: Services.Update.AppVersion.Current(),
            check: ct => new Services.Update.ReleaseMetaClient(updateHttp, updateOptions, Services.HostLog.Write).FetchLatestAsync(UpdateRid(), updatePkgKind, ct),
            download: (meta, ct) => new Services.Update.InstallerDownloader(updateHttp, Services.HostLog.Write).DownloadAsync(
                meta, updatesDir, TimeSpan.FromMinutes(updateOptions.DownloadTimeoutMinutes), ct),
            install: async (assetPath, version, ct) =>
            {
                // 安装时点自 release SHA256SUMS 复取期望哈希（HTTPS 直达仓库，用户空间改写不了）：
                // root 侧装前复验对照它——落盘的任何哈希都可被同权限改写，唯有 release 侧值是锚点。
                // 离线时此处抛出拒装（状态机回 ready），不无复核安装。
                string expectedSha = await new Services.Update.InstallerDownloader(updateHttp, Services.HostLog.Write)
                    .FetchSha256Async(updateOptions.Repository, version, Path.GetFileName(assetPath), ct);
                // 授权通过（LaunchAsync 观察窗口内未取消）后：主动关闭窗口让进程退出，
                // 安装脚本的等待环随即放行 rpm/dpkg 并拉起新版。缺这步脚本会死等本进程。
                await Services.Update.UpdateInstaller.LaunchAsync(assetPath, updatesDir, expectedSha, ct, log: Services.HostLog.Write);
                Services.HostLog.Write("[update] 授权通过，关闭应用以继续安装…");
                // 安装路径与托盘退出共用闸门：先批准，Close 才不会被 hide-to-tray 拦截转成隐藏
                _closeGate.ApproveExit();
                try
                {
                    _updateWindow?.Current?.Close();
                }
                catch (Exception ex)
                {
                    Services.HostLog.Write($"[update] 窗口关闭失败：{ex.Message}");
                }

                // 兜底：8 秒内仍未退出（Close 事件丢失等）则强制退出，保证安装流程放行
                StartExitFallback(ct);
            },
            persistence: new Services.Update.FileReadyPersistence(updatesDir),
            onTransition: state =>
            {
                // 自更新链路留痕：每次状态变化进 host.log（stdout 不可见教训的统一收口）
                Services.HostLog.Write(
                    "[update] " + state.Status
                    + (state.Version is null ? "" : $" {state.Version}")
                    + (state.Message is null ? "" : $"：{state.Message}"));
                PushUpdateState(_updateWindow, state);
            });
        Services.HostLog.Write($"[host] 自更新：当前版本 {Services.Update.AppVersion.Current()}，RID {UpdateRid()}，包类型 {updatePkgKind ?? "(n/a)"}，目录 {updatesDir}，feed 超时 {updateOptions.FeedTimeoutSeconds}s 下载超时 {updateOptions.DownloadTimeoutMinutes}m");
    }

    private void RunBootstrapIfNeeded()
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），后台任务完成
        // 检测/下载/安装/验证状态机，成功后绑定运行时、起 dsh 并导航进主界面；失败推错误态等待
        // 用户重试（desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控
        // （见各自插入点）——依赖序即插入位。
        if (_bootstrapNeeded)
        {
            _bootstrapSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bootstrapCts = new CancellationTokenSource();
            CancellationToken bootCt = _bootstrapCts.Token;
            _ = Task.Run(() => RunBootstrapTaskAsync(bootCt));
        }
    }

    /// <summary>后台引导任务（RunBootstrapIfNeeded 的 Task.Run 主体）：重试循环 → 绑定运行时 → 插件安装
    /// → 起 dsh → 导航进主界面；任一失败收口为日志（窗口仍可重试/关闭）。</summary>
    private async Task RunBootstrapTaskAsync(CancellationToken bootCt)
    {
        try
        {
            (string NodeExe, string DshEntry)? runtime = await RunBootstrapWithRetryAsync(_bootstrapGate, bootCt);
            if (runtime is null)
            {
                Services.HostLog.Write("[bootstrap] 引导未完成（用户放弃或应用退出）");
                return;
            }

            _bundledClosure = runtime;
            _host.BindRuntime(runtime.Value);

            // CLI shim 注册（ADR reference-alignment 批次四）：引导完成后运行时已下载就位，
            // 把 dsh/pnpm 注册进用户 PATH。best-effort（见 RegisterCliShim，内含兜底）。
            RegisterCliShim(_isDev);

            await InstallBootstrapPluginsAsync(runtime.Value, bootCt);

            Uri? url = await _host.StartAsync(timeout: TimeSpan.FromSeconds(60), bootCt);
            if (url is null)
            {
                Services.HostLog.Write($"[bootstrap] 引导完成但 dsh 未在时限内给出 URL。stderr 尾巴：\n{string.Join('\n', _host.StderrTail.TakeLast(8))}");
                return;
            }

            Services.HostLog.Write($"[host] runtime = {_host.RuntimeDescription}");
            Services.HostLog.Write($"[host] dsh web = {url}；从引导页导航进入主界面");
            _webUrl = url;
            await _windowAccessor.Current.NavigateAsync(url);
        }
        catch (OperationCanceledException)
        {
            Services.HostLog.Write("[bootstrap] 引导任务随应用退出取消");
        }
        catch (Exception ex)
        {
            // 后台引导任务的兜底收口：任何意外异常都不拖垮壳（窗口仍在，可重试或关闭）
            Services.HostLog.Write($"[bootstrap] 引导任务意外失败：{ex.Message}");
        }
        finally
        {
            _bootstrapSettled!.TrySetResult();
        }
    }

    /// <summary>引导刚落定后的插件装配（ADR reference-alignment 批次一/二）：companion（internal）spawn 前
    /// 静默自愈，dshmarket（preset）经引导页确认装/跳过。best-effort：失败只告警不阻断启动。</summary>
    private async Task InstallBootstrapPluginsAsync((string NodeExe, string DshEntry) runtime, CancellationToken bootCt)
    {
        // 对齐参照：companion（internal）在 spawn dsh 前静默自愈（batch-1），不出现在
        // 引导勾选清单（对齐 ensure_internal_plugins）；best-effort：失败只告警不阻断
        // （缺 companion 不阻塞 dsh 起动，下次启动自愈）。
        try
        {
            await Services.MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync(
                runtime.NodeExe,
                runtime.DshEntry,
                HarnessRuntimeHost.ResolveDshHome(),
                Path.Combine(AppContext.BaseDirectory, "resources", "plugins"),
                Services.HostLog.Write,
                Services.PluginProcessRunner.RunAsync,
                bootCt);
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[host] 引导：随包插件安装失败（跳过）：{ex.Message}");
        }

        // 首启插件引导（ADR reference-alignment 批次二）：dshmarket（preset）经引导页
        // chip 确认/跳过 + 日志回流；用户确认后才装（StartAsync 前，与 batch-1 合流）。
        // 跳过则该次不装（less-bootstrapped，dsh 起动后可从应用内市场/设置自愈补装）。
        try
        {
            await RunPreinstallPhaseAsync(runtime, RunDshPluginAddStreamingAsync, bootCt);
        }
        catch (Exception ex)
        {
            Services.HostLog.Write($"[host] 插件引导异常跳过：{ex.Message}");
        }
    }

    private void SetupSupervisor()
    {
        // 原 `using var supervisorCts`：生命周期由 Run 的 finally 释放（本方法赋值）。
        _supervisorCts = new CancellationTokenSource();
        _supervisorCtsRef = _supervisorCts; // 自更新后台任务 token 持有器接线（见顶部声明）
        // 启动期横幅导航门控（ADR shell-firstboot-hardening）：安装任务触发监督器重启后页面会被
        // NavigateAsync 整体替换，横幅必须等这次导航落地再注入，否则与导航竞速被清掉
        // （v0.3.0 实机：18:30:01 注入 vs 18:30:03 导航，旧 home 横幅陪葬）。
        _startupNavigationSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    Services.RecoveryPageBuilder.BuildScript("运行时进程意外退出，正在自动重启", tail));
                return ValueTask.CompletedTask;
            },
            // 崩溃恢复导航同步刷新 webUrl——健康监视器（有界恢复）靠它作为 reload 靶点；若
            // 崩溃重启用新端口（ADDR child-process-reaping-port-drift 的端口漂移）而 webUrl
            // 仍指向旧 URL，监视器的 reload 会打到已死的旧端口、甚至覆写刚恢复的导航。
            navigate: url =>
            {
                _webUrl = url;
                return _windowAccessor.Current.NavigateAsync(url);
            },
            log: Services.HostLog.Write);
        // 引导期门控：宿主尚无 dsh 进程时 WaitForExitAsync 立即完成，监督器会空转进恢复循环
        // 并用恢复屏覆写引导页——必须等引导落定（成功 spawn 或确认放弃）才进入监视。
        _supervisorTask = Task.Run(async () =>
        {
            if (_bootstrapSettled is not null)
            {
                try
                {
                    await _bootstrapSettled.Task.WaitAsync(_supervisorCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await _supervisor.RunAsync(_supervisorCts.Token);
        });

        // 「导航已到达」信号（ADR ryn-navigation-callbacks）：由 RynNavigationCallbacks 的
        // WebViewNavigated 回调在内容实际提交后触发，取代 RuntimeSupervisor.onNavigated 的
        // 「NavigateAsync 返回即触发」——恢复/横幅门控据此拿到真正的「页面已到达」。
        _app.Services.GetRequiredService<Services.RynNavigationCallbacks>().SetOnNavigated(
            () => _startupNavigationSettled.TrySetResult());

        // 有序退出编排 + 自更新兜底收割器接线（WireExitHandlers）
        WireExitHandlers(_app.Services.GetRequiredService<IRynWindow>());
    }

    /// <summary>有序退出编排 + 自更新兜底收割器接线（ADR child-process-reaping-port-drift /
    /// self-update-exit-reaps-dsh-child）：运行时回收先于关窗，8s 看门狗把静默滞留变成确定性终结。</summary>
    private void WireExitHandlers(IRynWindow quitWindow)
    {
        // 有序退出编排：运行时回收先于关窗，不依赖 GTK loop 对隐藏态窗口 close 的行为；8s 看门狗把
        // 静默滞留变成确定性终结。正常路径 Run 返回后 Run 尾部重复 Cancel/Stop/Release 均幂等，双路径收敛。
        _orderlyQuit = () =>
        {
            Services.HostLog.Write("[tray] 有序退出：回收运行时后关闭窗口");
            Services.ExitOrchestration.OrderlyQuit(
                () => _supervisorCts.Cancel(),
                _host.Stop,
                () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), _marker.Token),
                () => _instanceListener?.Dispose(),
                quitWindow.Close,
                StartQuitWatchdog);
        };

        // 自更新兜底退出收割器：经回收三件套单点（ExitOrchestration.ReapRuntime），不带关窗与看门狗——
        // 自更新路径由 pkexec 脚本接管进程接力，关窗已由 install 委托直退，此处只保证 dsh 不泄漏。
        // 与 orderlyQuit 同款「先回收再退」；StartExitFallback 触发时 supervisorCts/host/marker 均已就绪。
        _updateExitReaper = () =>
        {
            Services.HostLog.Write("[update] 兜底回收：cancel 监督器 + 整树击杀 dsh + 释放 marker");
            Services.ExitOrchestration.ReapRuntime(
                () => _supervisorCts.Cancel(),
                _host.Stop,
                () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), _marker.Token));
        };
    }

    private void SetupHealthMonitor()
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
            Services.HostLog.Write,
            reload: ct => _webUrl is null
                ? ValueTask.CompletedTask
                : _windowAccessor.Current.NavigateAsync(_webUrl, ct));
        _ = _healthMonitor.RunAsync(TimeSpan.FromSeconds(10), _supervisorCts.Token);
    }

    private void StartUpdateCheck()
    {
        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        if (_updateMachine is not null)
        {
            // 就绪横幅（批次三）：ready 到达一次性提示（订阅在窗口句柄就绪后建立，去重防重试期反复弹）
            _updateMachine.Subscribe(state =>
            {
                if (state.Status == Services.Update.UpdateStatus.Ready &&
                    state.Version is not null && !_readyNotified)
                {
                    _readyNotified = true;
                    _ = Services.PagePump.ShowBannerWhenReadyAsync(
                        _windowAccessor,
                        Services.Update.UpdateBanner.ReadyScript(state.Version, _uiLocale),
                        _supervisorCts.Token);
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await _updateMachine.StartAsync(_supervisorCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Services.HostLog.Write($"[update] start 失败：{ex.Message}");
                }
            });
        }
    }

    private void SharedHomeBannerTask()
    {
        // 共享 home 切换的启动期告知（ADR shared-home-desktop-profile）：版本底线检查 + 旧 home 一次性提示。
        // 随包插件现于 spawn dsh 前安装（不再「启动后装 → 覆写页面并重启运行时」），横幅无需等安装收尾，
        // 只需等首启引导落定——版本探针用的 bundledClosure 在引导完成时被赋值，提前跑会探到空。
        _ = Task.Run(async () =>
        {
            try
            {
                if (_bootstrapSettled is not null)
                {
                    await _bootstrapSettled.Task.WaitAsync(TimeSpan.FromSeconds(120), _supervisorCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                // 引导链路迟迟未定也照常告知：横幅是增强信息，不能因异常路径永久缺席
            }

            string home = HarnessRuntimeHost.ResolveDshHome();
            string? detected = await Services.RuntimeVersionGate.ProbeAsync(_bundledClosure, _supervisorCts.Token);
            if (detected is not null)
            {
                Services.HostLog.Write($"[host] dsh 版本 {detected}（底线 {Services.RuntimeVersionGate.MinimumVersion}）");
                if (Services.RuntimeVersionGate.IsBelowFloor(detected))
                {
                    Services.HostLog.Write($"[host] 警告：dsh {detected} 低于支持底线 {Services.RuntimeVersionGate.MinimumVersion}，已提示用户");
                    await Services.PagePump.ShowBannerWhenReadyAsync(_windowAccessor, Services.RuntimeVersionGate.BelowFloorBannerScript(detected, _uiLocale), _supervisorCts.Token);
                }
            }
            else
            {
                Services.HostLog.Write("[host] dsh 版本探测失败，跳过底线检查");
            }

            // 旧 home 留痕仅进日志（界面横幅已按用户拍板去除，ADR companion-settings-consolidation）；
            // 指回旧目录时不记「改用新目录」——自相矛盾且无信息量
            if (LegacyHomeNotice.IsPresent() && !PathsEqual(home, LegacyHomeNotice.LegacyPrivateHome))
            {
                Services.HostLog.Write($"[host] 检测到旧版桌面数据目录 {LegacyHomeNotice.LegacyPrivateHome}；新版使用 {home}（未迁移）");
            }

            // 上轮非受控退出：提示但不暗示应用故障（用户杀进程也属此类），引导导出诊断
            if (_previousRunUnclean)
            {
                await Services.PagePump.ShowBannerWhenReadyAsync(_windowAccessor, RunMarker.UncleanBannerScript(_uiLocale), _supervisorCts.Token);
            }
        });
    }
}
