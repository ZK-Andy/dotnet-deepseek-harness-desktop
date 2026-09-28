namespace DeepSeek.Harness.Desktop.Core.Update;

/// <summary>
/// 自更新协调器（Application 用例，ADR update-coordinator-core-port）：自更新栈的装载、就绪横幅订阅
/// 与后台检查。栈协作（宿主事实/feed/下载/安装）经四端口注入，UI 交接（状态推送/就绪横幅/退出批准/
/// 关窗/兜底强退）经委托注入——编排策略（dev 门禁、对账清扫时机、装前 SHA 复取序、ready 横幅去重）
/// 纯依赖可单测。Core 状态机不动，HttpClient 构造与平台判定在 Infrastructure 适配器。
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly bool _isDev;
    private readonly IUpdateEnvironment _environment;
    private readonly IReleaseFeed _feed;
    private readonly IPackageDownloader _downloader;
    private readonly IPackageInstaller _installer;
    private readonly Func<CancellationToken> _supervisorToken;
    private readonly Action<UpdateState> _pushState;
    private readonly Func<string, CancellationToken, Task> _showReadyBanner;
    private readonly Action _approveExit;
    private readonly Action _closeWindow;
    private readonly Action<CancellationToken> _scheduleExitFallback;
    private readonly Action<string>? _log;
    private UpdateStateMachine? _machine;
    private bool _readyNotified;

    /// <summary>创建协调器（状态机由 <see cref="Load"/> 按 dev 门禁装载）。</summary>
    /// <param name="isDev">是否 dev 运行时（dev 门禁；dev 默认不装载，防 dev 版本循环）。</param>
    /// <param name="environment">宿主环境端口（配置/目录/平台判定/对账清扫/持久化工厂）。</param>
    /// <param name="feed">release feed 端口（状态机 check 委托的落点）。</param>
    /// <param name="downloader">下载端口（状态机 download 委托与装前 SHA 复取的落点）。</param>
    /// <param name="installer">安装器端口（状态机 install 委托的落点）。</param>
    /// <param name="supervisorToken">后台长任务取消令牌来源（监督器 token）。</param>
    /// <param name="pushState">状态推送交接（每次状态变化推给插件 UI；窗口未就绪由实现方静默丢弃）。</param>
    /// <param name="showReadyBanner">就绪横幅交接（ready 到达且去重通过时调用，参数为目标版本与取消令牌）。</param>
    /// <param name="approveExit">退出批准交接（关窗闸门：先批准再 Close，放行 hide-to-tray 拦截）。</param>
    /// <param name="closeWindow">主动关窗交接（让进程退出以放行安装脚本的等待环）。</param>
    /// <param name="scheduleExitFallback">安装授权通过后的兜底强退调度（单实例退出管道）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public UpdateCoordinator(
        bool isDev,
        IUpdateEnvironment environment,
        IReleaseFeed feed,
        IPackageDownloader downloader,
        IPackageInstaller installer,
        Func<CancellationToken> supervisorToken,
        Action<UpdateState> pushState,
        Func<string, CancellationToken, Task> showReadyBanner,
        Action approveExit,
        Action closeWindow,
        Action<CancellationToken> scheduleExitFallback,
        Action<string>? log = null)
    {
        _isDev = isDev;
        _environment = environment;
        _feed = feed;
        _downloader = downloader;
        _installer = installer;
        _supervisorToken = supervisorToken;
        _pushState = pushState;
        _showReadyBanner = showReadyBanner;
        _approveExit = approveExit;
        _closeWindow = closeWindow;
        _scheduleExitFallback = scheduleExitFallback;
        _log = log;
    }

    /// <summary>自更新状态机；dev 门禁下为 null（不装载）。</summary>
    public UpdateStateMachine? Machine => _machine;

    /// <summary>装载自更新状态机（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）。
    /// 检查/下载/安装经端口委托注入；状态经 onTransition 推给插件 UI。dev 运行时不装载
    ///（除非 DSH_DESKTOP_UPDATE_FORCE=1 显式开启，避免 dev 版本循环）。</summary>
    public void Load()
    {
        // dev 门禁（判定在边界实现，含 DSH_DESKTOP_UPDATE_FORCE 环境读取）：dev 构建版本同 csproj，
        // 一旦比对出新 release，点击会把官方包装进系统后按 Environment.ProcessPath 拉起**旧 dev 二进制**，
        // 版本不变、ready 记录不清，形成循环（审核加固，见 ADR self-update-review-hardening）。
        bool enabled = _environment.IsEnabled(_isDev);
        _readyNotified = false;
        _machine = null;
        if (!enabled)
        {
            _log?.Invoke("[host] 自更新：dev 运行时不装载（DSH_DESKTOP_UPDATE_FORCE=1 可显式开启）");
            return;
        }

        UpdateOptions updateOptions = _environment.LoadOptions();
        string updatesDir = _environment.ResolveUpdatesDir(updateOptions.UpdatesDirName);
        string? updatePkgKind = _environment.DetectPackageKind();
        string updateRid = _environment.UpdateRid();
        string currentVersion = _environment.CurrentVersion();

        // 启动对账清扫（ADR self-update-prune-consumed-packages）：删过期包 + install.sh/.download.lock
        // 死残留；ready 待装包恒版本 > 当前（对账 ≤ 当前即清记录），天然免于误删。
        _environment.PruneStale(updatesDir, currentVersion);

        _machine = new UpdateStateMachine(
            currentVersion: currentVersion,
            check: ct => _feed.FetchLatestAsync(updateOptions, updateRid, updatePkgKind, ct),
            download: (meta, ct) => _downloader.DownloadAsync(
                meta, updatesDir, TimeSpan.FromMinutes(updateOptions.DownloadTimeoutMinutes), ct),
            install: async (assetPath, version, ct) =>
            {
                // 安装时点自 release SHA256SUMS 复取期望哈希（HTTPS 直达仓库，用户空间改写不了）：
                // root 侧装前复验对照它——落盘哈希可被同权限改写，唯 release 侧值是锚点；离线时此处抛出拒装（状态机回 ready）。
                string expectedSha = await _downloader
                    .FetchExpectedSha256Async(updateOptions.Repository, version, Path.GetFileName(assetPath), ct);
                // 授权通过（LaunchAsync 观察窗口内未取消）后：主动关闭窗口让进程退出，
                // 安装脚本的等待环随即放行 rpm/dpkg 并拉起新版。缺这步脚本会死等本进程。
                await _installer.LaunchAsync(assetPath, updatesDir, expectedSha, TimeSpan.FromSeconds(updateOptions.PkexecObserveSeconds), ct);
                _log?.Invoke("[update] 授权通过，关闭应用以继续安装…");
                // 安装路径与托盘退出共用闸门：先批准，Close 才不会被 hide-to-tray 拦截转成隐藏
                _approveExit();
                try
                {
                    _closeWindow();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[update] 窗口关闭失败：{ex.Message}");
                }

                // 兜底：8 秒内仍未退出（Close 事件丢失等）则强制退出，保证安装流程放行
                _scheduleExitFallback(ct);
            },
            persistence: _environment.CreateReadyPersistence(updatesDir),
            onTransition: state =>
            {
                // 自更新链路留痕：每次状态变化进 host.log（stdout 不可见教训的统一收口）
                _log?.Invoke(
                    "[update] " + state.Status
                    + (state.Version is null ? "" : $" {state.Version}")
                    + (state.Message is null ? "" : $"：{state.Message}"));
                _pushState(state);
            },
            log: _log);
        _log?.Invoke($"[host] 自更新：当前版本 {currentVersion}，RID {updateRid}，包类型 {updatePkgKind ?? "(n/a)"}，目录 {updatesDir}，feed 超时 {updateOptions.FeedTimeoutSeconds}s 下载超时 {updateOptions.DownloadTimeoutMinutes}m");
    }

    /// <summary>启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）；就绪横幅订阅在此建立。</summary>
    public void Start()
    {
        if (_machine is not { } machine)
        {
            return;
        }

        // 就绪横幅（批次三）：ready 到达一次性提示（订阅在窗口句柄就绪后建立，去重防重试期反复弹）
        machine.Subscribe(state =>
        {
            if (state.Status == UpdateStatus.Ready &&
                state.Version is not null && !_readyNotified)
            {
                _readyNotified = true;
                _ = _showReadyBanner(state.Version, _supervisorToken());
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await machine.StartAsync(_supervisorToken());
            }
            catch (OperationCanceledException)
            {
                // 应用退出取消：后台检查静默收口（状态机随主流程撤销）
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[update] start 失败：{ex.Message}");
            }
        });
    }
}
