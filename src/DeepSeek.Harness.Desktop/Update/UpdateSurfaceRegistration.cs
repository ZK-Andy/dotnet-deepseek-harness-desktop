using DeepSeek.Harness.Desktop.Core.Ipc;

namespace DeepSeek.Harness.Desktop.Update;

/// <summary>新宿主（Avalonia）的自更新装配（ADR architecture/2026-10-23-avalonia-g3-update-surface）：
/// 协调器、三条命令路由与托盘「检查更新」的反馈面一次构造。旧宿主的同一栈装配留在
/// <c>Bootstrap/PreBuildRegistration</c> 的托盘/更新装配里，随迁移收尾下线。
/// 构造收在注册处（R1a）：基础设施适配器与协调器只在本文件 new，宿主侧只取用产物。</summary>
internal static class UpdateSurfaceRegistration
{
    /// <summary>构造更新面：协调器已按 dev 门禁 <c>Load</c> 定稿，路由与托盘动作按机器是否装载给值。</summary>
    /// <param name="isDev">是否 dev 运行时（dev 门禁；<c>DSH_DESKTOP_UPDATE_FORCE=1</c> 可显式开启）。</param>
    /// <param name="supervisorToken">后台长任务取消令牌来源（监督器；check 是分钟级任务，不挂 IPC 请求域）。</param>
    /// <param name="scriptTarget">页面脚本求值端口（状态推送与结论横幅的落点）。</param>
    /// <param name="pump">页面注入单点（横幅未就绪重试的单一实现）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点（结论横幅与按钮文案）。</param>
    /// <param name="recall">唤回主窗动作（结论落在页面，窗口藏在托盘时看不见）。</param>
    /// <param name="approveExit">退出批准（安装路径先放行关窗闸门）。</param>
    /// <param name="closeWindow">主动关窗（安装器派生后让进程退出，放行脚本等待环）。</param>
    /// <param name="scheduleExitFallback">安装授权后的兜底强退调度（退出管道）。</param>
    /// <param name="log">日志委托。</param>
    /// <returns>更新面（路由表、托盘动作与启动检查入口）。</returns>
    public static UpdateSurface Create(
        bool isDev,
        Func<CancellationToken> supervisorToken,
        IWindowScriptTarget scriptTarget,
        PagePump pump,
        UiLocale uiLocale,
        Action recall,
        Action approveExit,
        Action closeWindow,
        Action<CancellationToken> scheduleExitFallback,
        Action<string> log)
    {
        var presenter = new UpdateCheckPresenter(scriptTarget, pump, uiLocale, recall, log);
        var coordinator = new UpdateCoordinator(
            isDev,
            new UpdateEnvironmentAdapter(log),
            new ReleaseFeedAdapter(log),
            new PackageDownloaderAdapter(log),
            new UpdateInstaller(log),
            supervisorToken,
            state => PagePump.PushUpdateState(scriptTarget, state),
            presenter.ShowReadyBannerAsync,
            approveExit,
            closeWindow,
            scheduleExitFallback,
            log);
        coordinator.Load();
        return Wire(coordinator.Machine, coordinator.Start, presenter, supervisorToken, log);
    }

    /// <summary>机器 → 路由/托盘动作的映射（纯装配，无宿主依赖：两个分支都可单测）。
    /// 未装载（dev 门禁）时路由为空、动作 null——与旧宿主同判据「栈未装载即不出现」。</summary>
    /// <param name="machine">自更新状态机；null = 未装载。</param>
    /// <param name="start">启动检查入口（协调器 <c>Start</c>）。</param>
    /// <param name="presenter">结论反馈面。</param>
    /// <param name="supervisorToken">后台取消令牌来源。</param>
    /// <param name="log">日志委托。</param>
    /// <returns>更新面产物。</returns>
    internal static UpdateSurface Wire(
        UpdateStateMachine? machine,
        Action start,
        UpdateCheckPresenter presenter,
        Func<CancellationToken> supervisorToken,
        Action<string> log) =>
        machine is null
            ? new UpdateSurface(start, [], null)
            : new UpdateSurface(
                start,
                [new DesktopUpdateCommandRouter(machine, log, supervisorToken)],
                () => presenter.CheckInBackground(machine, supervisorToken));
}

/// <summary>新宿主的更新面产物：启动检查入口、命令路由与托盘「检查更新」动作。
/// 未装载（dev 门禁）时路由为空、动作 null——托盘项随之消失，与旧宿主同判据。</summary>
internal sealed class UpdateSurface
{
    private readonly Action _start;

    /// <summary>创建更新面。</summary>
    /// <param name="start">启动检查入口（协调器 <c>Start</c>；未装载时是 no-op）。</param>
    /// <param name="routers">自更新命令路由（未装载时为空）。</param>
    /// <param name="checkUpdate">托盘「检查更新」动作（未装载时 null）。</param>
    public UpdateSurface(Action start, IReadOnlyList<ICommandRouter> routers, Action? checkUpdate)
    {
        _start = start;
        Routers = routers;
        CheckUpdate = checkUpdate;
    }

    /// <summary>自更新命令路由（<c>desktop.update.getState/check/install</c>）。</summary>
    public IReadOnlyList<ICommandRouter> Routers { get; }

    /// <summary>托盘「检查更新」动作；null = 自更新栈未装载。</summary>
    public Action? CheckUpdate { get; }

    /// <summary>启动对账与后台检查一次（就绪横幅订阅同时建立；窗口未就绪由注入单点重试）。</summary>
    public void Start() => _start();
}

/// <summary>自更新结论的宿主反馈面（ADR avalonia-g3-update-surface）：Avalonia 托盘没有系统通知面，
/// 托盘触发的检查结论改为「唤回主窗 + 页内横幅」——就绪复用就绪横幅（同一 id，与协调器自身的订阅
/// 天然去重），已是最新与失败落检查横幅（同名替换，连续两次结论不同时刷新）。
/// 中间态（检查中/下载中）不打扰。</summary>
internal sealed class UpdateCheckPresenter
{
    private readonly IWindowScriptTarget _target;
    private readonly PagePump _pump;
    private readonly UiLocale _uiLocale;
    private readonly Action _recall;
    private readonly Action<string> _log;

    /// <summary>创建反馈面。</summary>
    /// <param name="target">页面脚本求值端口。</param>
    /// <param name="pump">页面注入单点（未就绪重试）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点。</param>
    /// <param name="recall">唤回主窗动作（结论可见化）。</param>
    /// <param name="log">日志委托。</param>
    public UpdateCheckPresenter(
        IWindowScriptTarget target,
        PagePump pump,
        UiLocale uiLocale,
        Action recall,
        Action<string> log)
    {
        _target = target;
        _pump = pump;
        _uiLocale = uiLocale;
        _recall = recall;
        _log = log;
    }

    /// <summary>协调器的就绪横幅交接：与检查结论共用同一脚本（同 id 去重，先到者胜）。</summary>
    /// <param name="version">就绪的目标版本。</param>
    /// <param name="ct">取消令牌（监督器/退出）。</param>
    /// <returns>注入完成的任务。</returns>
    public Task ShowReadyBannerAsync(string version, CancellationToken ct) =>
        _pump.ShowBannerWhenShellReadyAsync(
            _target,
            DesktopBanner.ClearScript(DesktopBanner.UpdateCheckBannerId)
            + UpdateBanner.ReadyScript(version, _uiLocale),
            WebAuthRecovery.HolderMarker,
            ct);

    /// <summary>托盘「检查更新」：后台跑一次检查（并发去重归状态机），结束时交付结论。</summary>
    /// <param name="machine">自更新状态机。</param>
    /// <param name="supervisorToken">后台取消令牌来源（监督器；请求帧返回后不连坐）。</param>
    public void CheckInBackground(UpdateStateMachine machine, Func<CancellationToken> supervisorToken) =>
        _ = CheckInBackgroundAsync(machine, supervisorToken);

    /// <summary>同上，可等待形态（测试用：断言令牌源已释放/已取消时不注入、不上抛）。</summary>
    /// <param name="machine">自更新状态机。</param>
    /// <param name="supervisorToken">后台取消令牌来源。</param>
    /// <returns>检查与结论交付完成的任务（异常已在其内收口，不再上抛）。</returns>
    internal Task CheckInBackgroundAsync(UpdateStateMachine machine, Func<CancellationToken> supervisorToken) =>
        Task.Run(async () =>
        {
            try
            {
                // 令牌在 try 内取：监督器令牌源可能已随 ShellResources 释放（取 token 抛 ObjectDisposedException）
                CancellationToken ct = supervisorToken();
                UpdateState state = await machine.CheckAsync(ct).ConfigureAwait(false);
                await PresentAsync(state, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 退出/关闭取消：结论面随窗口消失，不留半张横幅
            }
            catch (Exception ex)
            {
                // 状态机把失败转成 error 态返回，走到这里的是宿主侧异常：留痕不吞
                _log($"[update] 检查更新异常：{ex.Message}");
            }
        }, CancellationToken.None);

    /// <summary>交付一次检查结论：唤回主窗后注入对应横幅（中间态与未知态不打扰）。</summary>
    /// <param name="state">检查结束态（状态机已补齐 <c>Current</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>注入完成的任务。</returns>
    public async Task PresentAsync(UpdateState state, CancellationToken ct)
    {
        // 退出期不再把窗口从托盘拽出来（后面注入会零尝试返回，只剩一次无意义的唤回）
        if (ct.IsCancellationRequested)
        {
            return;
        }

        // 就绪与协调器自身的 ready 订阅共用同一脚本（同 id 先到者胜），不另造第二张；中间态不打扰。
        if (state.Status == UpdateStatus.Ready && state.Version is not null)
        {
            _recall();
            await ShowReadyBannerAsync(state.Version, ct).ConfigureAwait(false);
            return;
        }

        string? text = TrayCheckFeedback.Message(state, _uiLocale.Language);
        if (text is null)
        {
            return;
        }

        // 先唤回再注入：横幅只有窗口可见时才有意义（托盘触发的检查多半是从隐藏态发起的）
        _recall();
        // 只有确认「已是最新」才清就绪横幅（ready 因此失效）；失败不清——网络抖动不该抹掉可操作的就绪提示
        string script = (state.Status == UpdateStatus.UpToDate
                ? DesktopBanner.ClearScript(DesktopBanner.UpdateReadyBannerId)
                : string.Empty)
            + DesktopBanner.Build(
                DesktopBanner.UpdateCheckBannerId,
                text,
                state.Status == UpdateStatus.Error ? DesktopBanner.BannerTone.Warn : DesktopBanner.BannerTone.Neutral,
                _uiLocale.OkLabel,
                replaceExisting: true);
        await _pump.ShowBannerWhenShellReadyAsync(_target, script, WebAuthRecovery.HolderMarker, ct).ConfigureAwait(false);
    }
}
