namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 自更新状态机（opencode <c>updater-controller</c> 的 C# 移植）：
/// <c>idle→checking→downloading→ready→installing</c>，外加 up-to-date/error；
/// ready 持久化跨启动，启动对账后自动检查一次；检查/下载失败转 error 不阻塞后续手动重查。
/// 后端动作全部委托注入——纯逻辑可 xunit 单测。
/// </summary>
public sealed class UpdateStateMachine
{
    /// <summary>检查动作：返回最新版本元数据；null 表示无可用更新。</summary>
    public delegate Task<ReleaseMeta?> CheckDelegate(CancellationToken cancellationToken);

    /// <summary>下载动作：落地安装包并校验，返回本地文件路径。</summary>
    public delegate Task<string> DownloadDelegate(ReleaseMeta meta, CancellationToken cancellationToken);

    /// <summary>安装动作：执行安装器并重启应用（成功则进程即将退出）。</summary>
    public delegate Task InstallDelegate(string assetPath, string version, CancellationToken cancellationToken);

    /// <summary>ready 记录持久化（跨启动恢复「可安装」提示）。</summary>
    public interface IPersistence
    {
        /// <summary>读取持久化的 ready 记录；无则 null。</summary>
        Task<ReadyRecord?> GetAsync(CancellationToken cancellationToken);

        /// <summary>写入 ready 记录。</summary>
        Task SetAsync(ReadyRecord record, CancellationToken cancellationToken);

        /// <summary>清除记录（已装完或确认无更新）。</summary>
        Task ClearAsync(CancellationToken cancellationToken);
    }

    /// <summary>ready 持久化记录。</summary>
    /// <param name="Version">目标版本号。</param>
    /// <param name="AssetPath">已下载安装包的本地路径。</param>
    public sealed record ReadyRecord(string Version, string AssetPath);

    private readonly string _currentVersion;
    private readonly CheckDelegate _check;
    private readonly DownloadDelegate _download;
    private readonly InstallDelegate _install;
    private readonly IPersistence _persistence;
    private readonly Action<UpdateState>? _onTransition;

    private UpdateState _state = new(UpdateStatus.Idle);
    private Task? _pending;
    private readonly object _checkGate = new();
    private readonly List<Action<UpdateState>> _listeners = [];

    /// <summary>创建状态机。</summary>
    /// <param name="currentVersion">当前应用版本（用于启动对账与「无更新」判定）。</param>
    /// <param name="check">检查委托。</param>
    /// <param name="download">下载委托。</param>
    /// <param name="install">安装委托。</param>
    /// <param name="persistence">ready 持久化。</param>
    /// <param name="onTransition">每次状态变化的回调（UI 推送）；回调异常由状态机兜住并记日志。</param>
    public UpdateStateMachine(
        string currentVersion,
        CheckDelegate check,
        DownloadDelegate download,
        InstallDelegate install,
        IPersistence persistence,
        Action<UpdateState>? onTransition = null)
    {
        _currentVersion = currentVersion;
        _check = check;
        _download = download;
        _install = install;
        _persistence = persistence;
        _onTransition = onTransition;
    }

    /// <summary>当前状态快照。</summary>
    public UpdateState State => _state;

    /// <summary>订阅状态变化；立即回调一次当前状态。返回退订委托。</summary>
    public IDisposable Subscribe(Action<UpdateState> listener)
    {
        _listeners.Add(listener);
        listener(_state);
        return new Subscription(_listeners, listener);
    }

    /// <summary>
    /// 启动：读持久化对账——记录版本**不高于**当前版本（刚装完/降级残留/损坏记录）则清除；
    /// 高于当前且安装包仍在时直接回 ready（不重复下载）；随后台检查一次（失败静默转 error，不影响首屏）。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ReadyRecord? ready = await _persistence.GetAsync(cancellationToken).ConfigureAwait(false);
        if (ready is not null && ShouldClearReady(ready.Version))
        {
            await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (ready is not null && File.Exists(ready.AssetPath))
        {
            // 跨启动仍有有效安装包：直接回 ready，不重复下载
            Transition(new UpdateState(UpdateStatus.Ready, ready.Version));
            return;
        }

        await CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对账判定：记录版本不高于当前，或记录损坏（无法解析）——都按残留清除并重查。</summary>
    private bool ShouldClearReady(string recordedVersion)
    {
        try
        {
            return UpdateVersion.Compare(recordedVersion, _currentVersion) <= 0;
        }
        catch (ArgumentException)
        {
            // 记录里的版本串无法解析：视同残留清除，不让它卡死启动
            return true;
        }
    }

    /// <summary>
    /// 检查一次更新；已在 ready 或有进行中的检查时直接返回当前状态（并发去重——
    /// 判空与占位在同一临界区内，多个并发调用只跑一次检查）。
    /// </summary>
    public async Task<UpdateState> CheckAsync(CancellationToken cancellationToken)
    {
        if (_state.Status is UpdateStatus.Ready or UpdateStatus.Installing)
        {
            return _state;
        }

        Task pending;
        bool joined;
        lock (_checkGate)
        {
            if (_pending is not null)
            {
                pending = _pending;
                joined = true;
            }
            else
            {
                pending = RunCheckAsync(cancellationToken);
                _pending = pending;
                joined = false;
            }
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        finally
        {
            if (!joined)
            {
                lock (_checkGate)
                {
                    if (ReferenceEquals(_pending, pending))
                    {
                        _pending = null;
                    }
                }
            }
        }

        return _state;
    }

    private async Task RunCheckAsync(CancellationToken cancellationToken)
    {
        Transition(new UpdateState(UpdateStatus.Checking));
        try
        {
            ReleaseMeta? meta = await _check(cancellationToken).ConfigureAwait(false);
            if (meta is null || UpdateVersion.Compare(meta.Version, _currentVersion) <= 0)
            {
                await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
                Transition(new UpdateState(UpdateStatus.UpToDate));
                return;
            }

            Transition(new UpdateState(UpdateStatus.Downloading, meta.Version));
            string path = await _download(meta, cancellationToken).ConfigureAwait(false);
            await _persistence.SetAsync(new ReadyRecord(meta.Version, path), cancellationToken).ConfigureAwait(false);
            Transition(new UpdateState(UpdateStatus.Ready, meta.Version));
        }
        catch (OperationCanceledException)
        {
            // 应用退出导致的取消：回 idle 等下次检查
            Transition(new UpdateState(UpdateStatus.Idle));
            throw;
        }
        catch (Exception ex)
        {
            Transition(new UpdateState(UpdateStatus.Error, Message: ex.Message));
        }
    }

    /// <summary>
    /// 从 ready 安装并重启；仅 ready 态允许。安装器已派生后进程即退出，
    /// 失败则回退 ready 并抛出。
    /// </summary>
    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        if (_state.Status != UpdateStatus.Ready || _state.Version is null)
        {
            throw new InvalidOperationException("Update is not ready to install");
        }

        string version = _state.Version;
        ReadyRecord? record = await _persistence.GetAsync(cancellationToken).ConfigureAwait(false);
        if (record is null || !File.Exists(record.AssetPath))
        {
            await _persistence.ClearAsync(cancellationToken).ConfigureAwait(false);
            Transition(new UpdateState(UpdateStatus.Idle));
            throw new InvalidOperationException("Ready asset is missing; state reset");
        }

        Transition(new UpdateState(UpdateStatus.Installing, version));
        try
        {
            await _install(record.AssetPath, version, cancellationToken).ConfigureAwait(false);
            // 成功路径：进程随安装流程退出，不再迁移状态
        }
        catch (Exception)
        {
            Transition(new UpdateState(UpdateStatus.Ready, version));
            throw;
        }
    }

    private void Transition(UpdateState next)
    {
        // Current 由状态机单点补齐：页面任何状态帧都带当前版本，更新页免二次查询；
        // 状态存取与全部回调（推送/订阅者）统一发补齐后的帧，避免内外不一致
        UpdateState effective = next.Current is null && _currentVersion is not null
            ? next with { Current = _currentVersion }
            : next;
        _state = effective;
        try
        {
            _onTransition?.Invoke(effective);
        }
        catch (Exception ex)
        {
            // 宿主推送回调（窗口未就绪/已销毁等）失败不拖垮状态机；与订阅者同等待遇。
            // 落盘收口：桌面形态 stdout 不可见（v0.2.1 实证），此类异常面必须进 host.log 才能排查
            Services.HostLog.Write($"[update] 状态推送回调失败：{ex.Message}");
        }

        foreach (Action<UpdateState> listener in _listeners)
        {
            try
            {
                listener(effective);
            }
            catch (Exception ex)
            {
                // 单个订阅者（如 UI 推送在窗口未就绪时）失败不拖垮状态机
                Services.HostLog.Write($"[update] 状态回调失败：{ex.Message}");
            }
        }
    }

    private sealed class Subscription(List<Action<UpdateState>> listeners, Action<UpdateState> listener) : IDisposable
    {
        public void Dispose() => listeners.Remove(listener);
    }
}
