using System.Runtime.InteropServices;
using DeepSeek.Harness.Desktop.Services.Tray;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 壳层更新协调器（ADR composition-root-value-flow-pipeline 批次 1）：自更新栈的装载、就绪横幅订阅
/// 与后台检查从组合根下沉为构造注入的真类型服务。Core 状态机与 Infrastructure 客户端不动，
/// HttpClient 构造随协调器迁出组合根（D005 主工程豁免面收缩）。
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly LaunchOptions _launch;
    private readonly Func<CurrentWindowAccessor?> _windowAccessor;
    private readonly CloseGate _closeGate;
    private readonly UiLocale _uiLocale;
    private readonly Func<CancellationToken> _supervisorToken;
    private readonly Action<CancellationToken> _scheduleExitFallback;
    private readonly Action<string>? _log;
    private UpdateStateMachine? _machine;
    private bool _readyNotified;

    /// <summary>创建协调器（状态机由 <see cref="Load"/> 按 dev 门禁装载）。</summary>
    /// <param name="launch">启动期 A 类配置（dev 判定；ADR composition-root-value-flow-pipeline 批次 3）。</param>
    /// <param name="windowAccessor">当前窗口访问器提供者；BuildApp 前为 null（状态推送静默丢弃）。</param>
    /// <param name="closeGate">关窗闸门：安装路径先批准再 Close，放行 hide-to-tray 拦截。</param>
    /// <param name="uiLocale">UI 语言单点（就绪横幅文案）。</param>
    /// <param name="supervisorToken">后台长任务取消令牌来源（监督器 token）。</param>
    /// <param name="scheduleExitFallback">安装授权通过后的兜底强退调度（单实例退出管道）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public UpdateCoordinator(
        LaunchOptions launch,
        Func<CurrentWindowAccessor?> windowAccessor,
        CloseGate closeGate,
        UiLocale uiLocale,
        Func<CancellationToken> supervisorToken,
        Action<CancellationToken> scheduleExitFallback,
        Action<string>? log = null)
    {
        _launch = launch;
        _windowAccessor = windowAccessor;
        _closeGate = closeGate;
        _uiLocale = uiLocale;
        _supervisorToken = supervisorToken;
        _scheduleExitFallback = scheduleExitFallback;
        _log = log;
    }

    /// <summary>自更新状态机；dev 门禁下为 null（不装载）。</summary>
    public UpdateStateMachine? Machine => _machine;

    /// <summary>装载自更新状态机（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）。
    /// 检查/下载/安装全部委托注入；状态经 CustomEvent 推给插件 UI。dev 运行时不装载
    ///（除非 DSH_DESKTOP_UPDATE_FORCE=1 显式开启，避免 dev 版本循环）。</summary>
    public void Load()
    {
        // 自更新栈（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）：
        // 状态机纯逻辑可单测，检查/下载/安装全部委托注入；状态经 CustomEvent 推给插件 UI。
        // dev 运行时不装载（除非 DSH_DESKTOP_UPDATE_FORCE=1 显式开启验证）：dev 构建版本同 csproj，
        // 一旦比对出新 release，点击会把官方包装进系统后按 Environment.ProcessPath 拉起**旧 dev 二进制**，
        // 版本不变、ready 记录不清，形成循环（审核加固，见 ADR self-update-review-hardening）。
        bool enabled = UpdateOptions.IsEnabledFor(
            _launch.IsDev,
            Environment.GetEnvironmentVariable(UpdateOptions.ForceDevEnv));
        _readyNotified = false;
        _machine = null;
        if (!enabled)
        {
            _log?.Invoke("[host] 自更新：dev 运行时不装载（DSH_DESKTOP_UPDATE_FORCE=1 可显式开启）");
            return;
        }

        var updateOptions = UpdateOptions.Load(AppContext.BaseDirectory);
        HttpClient updateHttp = UpdateHttpClient.Create();
        string updatesDir = Path.Combine(HarnessRuntimeHost.ResolveDshHome(), updateOptions.UpdatesDirName);
        string? updatePkgKind = UpdatePlatform.DetectCurrentPackageKind();

        // 启动对账清扫（ADR self-update-prune-consumed-packages）：删过期包 + install.sh/.download.lock
        // 死残留；ready 待装包恒版本 > 当前（对账 ≤ 当前即清记录），天然免于误删。
        string currentVersion = AppVersion.Current();
        StalePackagePruner.Run(updatesDir, currentVersion, log: _log);

        _machine = new UpdateStateMachine(
            currentVersion: currentVersion,
            check: ct => new ReleaseMetaClient(updateHttp, updateOptions, _log).FetchLatestAsync(UpdateRid(), updatePkgKind, ct),
            download: (meta, ct) => new InstallerDownloader(updateHttp, _log).DownloadAsync(
                meta, updatesDir, TimeSpan.FromMinutes(updateOptions.DownloadTimeoutMinutes), ct),
            install: async (assetPath, version, ct) =>
            {
                // 安装时点自 release SHA256SUMS 复取期望哈希（HTTPS 直达仓库，用户空间改写不了）：
                // root 侧装前复验对照它——落盘哈希可被同权限改写，唯 release 侧值是锚点；离线时此处抛出拒装（状态机回 ready）。
                string expectedSha = await new InstallerDownloader(updateHttp, _log)
                    .FetchSha256Async(updateOptions.Repository, version, Path.GetFileName(assetPath), ct);
                // 授权通过（LaunchAsync 观察窗口内未取消）后：主动关闭窗口让进程退出，
                // 安装脚本的等待环随即放行 rpm/dpkg 并拉起新版。缺这步脚本会死等本进程。
                await UpdateInstaller.LaunchAsync(assetPath, updatesDir, expectedSha, ct, log: _log);
                _log?.Invoke("[update] 授权通过，关闭应用以继续安装…");
                // 安装路径与托盘退出共用闸门：先批准，Close 才不会被 hide-to-tray 拦截转成隐藏
                _closeGate.ApproveExit();
                try
                {
                    _windowAccessor()?.Current?.Close();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[update] 窗口关闭失败：{ex.Message}");
                }

                // 兜底：8 秒内仍未退出（Close 事件丢失等）则强制退出，保证安装流程放行
                _scheduleExitFallback(ct);
            },
            persistence: new FileReadyPersistence(updatesDir),
            onTransition: state =>
            {
                // 自更新链路留痕：每次状态变化进 host.log（stdout 不可见教训的统一收口）
                _log?.Invoke(
                    "[update] " + state.Status
                    + (state.Version is null ? "" : $" {state.Version}")
                    + (state.Message is null ? "" : $"：{state.Message}"));
                PagePump.PushUpdateState(_windowAccessor(), state);
            },
            log: _log);
        _log?.Invoke($"[host] 自更新：当前版本 {currentVersion}，RID {UpdateRid()}，包类型 {updatePkgKind ?? "(n/a)"}，目录 {updatesDir}，feed 超时 {updateOptions.FeedTimeoutSeconds}s 下载超时 {updateOptions.DownloadTimeoutMinutes}m");
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
                _ = PagePump.ShowBannerWhenReadyAsync(
                    _windowAccessor()!,
                    UpdateBanner.ReadyScript(state.Version, _uiLocale),
                    _supervisorToken());
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
}
