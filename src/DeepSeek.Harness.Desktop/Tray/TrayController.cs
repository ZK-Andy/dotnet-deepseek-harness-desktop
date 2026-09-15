using Ryn.Core;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Tray;

/// <summary>
/// 壳层托盘控制器（ADR composition-root-value-flow-pipeline 批次 1）：托盘可用/就绪、图标、
/// hide-to-tray 拦截与唤回最大化采样从组合根下沉为构造注入的真类型服务。窗口与依赖以
/// 惰性委托注入——控制器在单实例仲裁前构造，窗口/Ryn 服务在 <c>BuildApp</c> 后才就绪。
/// </summary>
public sealed class TrayController
{
    private readonly Func<IRynWindow> _window;
    private readonly Func<CurrentWindowAccessor?> _accessor;
    private readonly CloseGate _closeGate;
    private readonly CloseBehaviorPreference _closeBehavior;
    private readonly UiLocale _uiLocale;
    private readonly UpdateStateMachine? _updateMachine;
    private readonly Action<string>? _log;
    private int _maximizedAtHide = -1;
    private string _iconPath = string.Empty;
    private bool _available;
    private bool _ready;
    private TrayService? _tray;

    /// <summary>创建托盘控制器。</summary>
    /// <param name="window">deferred 窗口代理提供者（Ryn DI 解析；调用时点均在 BuildApp 之后）。</param>
    /// <param name="accessor">当前窗口访问器提供者；BuildApp 前返回 null（启动早期激活请求静默忽略）。</param>
    /// <param name="closeGate">关窗闸门：hide-to-tray 拦截的唯一裁决点。</param>
    /// <param name="closeBehavior">关窗行为偏好（用户可选「关闭即退出」）。</param>
    /// <param name="uiLocale">UI 语言单点（菜单文案与切换重建）。</param>
    /// <param name="updateMachine">自更新状态机（决定「检查更新」菜单项是否出现）；已由协调器装载定稿。</param>
    /// <param name="log">日志回调（可选）。</param>
    public TrayController(
        Func<IRynWindow> window,
        Func<CurrentWindowAccessor?> accessor,
        CloseGate closeGate,
        CloseBehaviorPreference closeBehavior,
        UiLocale uiLocale,
        UpdateStateMachine? updateMachine,
        Action<string>? log = null)
    {
        _window = window;
        _accessor = accessor;
        _closeGate = closeGate;
        _closeBehavior = closeBehavior;
        _uiLocale = uiLocale;
        _updateMachine = updateMachine;
        _log = log;
    }

    /// <summary>系统托盘是否可用（icon 资产存在）。</summary>
    public bool IsAvailable => _available;

    /// <summary>托盘是否已就绪（Show 成功）。无托盘环境隐藏无从谈起，客户端据此禁用开关。</summary>
    public bool IsReady => _ready;

    /// <summary>托盘图标路径（Ryn 托盘注册与窗口 opts 共用）。</summary>
    public string IconPath => _iconPath;

    /// <summary>关窗闸门（hide-to-tray 唯一裁决点；自更新安装路径与恢复页退出共用）。</summary>
    public CloseGate CloseGate => _closeGate;

    /// <summary>关窗行为偏好（关闭按钮是否隐藏到托盘）。</summary>
    public CloseBehaviorPreference CloseBehavior => _closeBehavior;

    /// <summary>装配壳在探测 icon 资产后配置可用性与路径。</summary>
    /// <param name="iconPath">图标文件路径。</param>
    /// <param name="available">图标是否存在（不存在则托盘不注册，关窗保持直退）。</param>
    public void ConfigureIcon(string iconPath, bool available)
    {
        _iconPath = iconPath;
        _available = available;
    }

    /// <summary>托盘就绪化：装菜单并显示（<paramref name="tray"/> 为 null 表示无托盘环境）。
    /// 顺序契约：必须先 Show 再 SetMenu——Linux 后端在 Show 前尚未注册 StatusNotifierItem，
    /// SetMenu 经 <c>_item?.</c> 静默丢弃；macOS 的 RebuildMenu 在 status item 未创建时同样丢弃。
    /// 就绪后订阅语言切换重建菜单，并挂 <c>Closing</c> 拦截（hide-to-tray）。</summary>
    /// <param name="trayFactory">Ryn 托盘服务解析器；null = 无托盘环境。</param>
    public void Show(Func<TrayService>? trayFactory)
    {
        if (trayFactory is not null)
        {
            try
            {
                TrayService tray = trayFactory();
                tray.Show();
                tray.SetMenu(TrayMenuActions.BuildItems(includeUpdateItem: _updateMachine is not null, _uiLocale));
                _tray = tray;
                _ready = true;
                _log?.Invoke("[host] 系统托盘已注册");
                // dsh 语言切换 → companion 上报 → locale 变化即重建菜单（ADR host-ui-locale）
                _uiLocale.Changed += RebuildMenu;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[host] 系统托盘初始化失败，关闭窗口将直接退出：{ex.Message}");
            }
        }

        if (_ready)
        {
            // IRynWindow 是 deferred 代理：此处窗口尚未创建，Closing 订阅会被缓冲到窗口就绪后挂载。
            // 回调内绝不抛异常——上游对抛异常的 Closing 处理是「放行关窗」，比隐藏更危险。
            IRynWindow trayWindow = _window();
            trayWindow.Closing += (_, e) =>
            {
                if (!_closeGate.ShouldCancelClose || !_closeBehavior.HideOnClose)
                {
                    // 显式放行通道（托盘退出 / 自更新安装），或用户已选「关闭即退出」
                    return;
                }

                e.Cancel = true;
                _ = HideForTrayAsync(trayWindow);
            };
        }
    }

    /// <summary>hide-to-tray：先采样窗口态留证，再把窗口藏起来而非销毁。失败只留日志，不拖垮关窗链路。</summary>
    private async Task HideForTrayAsync(IRynWindow window)
    {
        try
        {
            // 原生查询 IRynWindow.IsMaximized（Ryn 0.30.3 起暴露，本仓自 0.30.4 消费）
            Volatile.Write(ref _maximizedAtHide, window.IsMaximized ? 1 : 0);
            // 隐藏即采样的留痕+主线程活性证据：唤回行为异常的排查需要知道「隐藏时看到什么」
            _log?.Invoke($"[tray] 窗口隐藏到托盘（隐藏前最大化采样={Volatile.Read(ref _maximizedAtHide)}）");
        }
        catch (Exception ex)
        {
            // deferred 代理在窗口未就绪时可能抛出：按未知处理，唤回路径对未知不动作
            Volatile.Write(ref _maximizedAtHide, -1);
            _log?.Invoke($"[tray] 最大化采样失败：{ex.Message}");
        }

        try
        {
            await window.HideAsync();
            _log?.Invoke("[tray] 窗口已隐藏");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[tray] 隐藏窗口失败：{ex.Message}");
        }
    }

    /// <summary>托盘唤回：样本入口即取本地快照，finally 无条件消费——若下方任一 await 抛出，
    /// 残留样本会让下一次托盘点击把用户手动还原的窗口误最大化。Linux 隐藏态预置 + 显示后兜底
    /// 确认两拍（ADR tray-recall-maximize-and-check-feedback）。</summary>
    public async Task RecallAsync()
    {
        IRynWindow trayWindow = _window();
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
                if (OperatingSystem.IsLinux() && TrayRecallMaximize.ShouldEnsure(sample))
                {
                    trayWindow.SetMaximized(true);
                    _log?.Invoke("[tray] 唤回：隐藏态已预置最大化");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[tray] 隐藏态预置最大化失败：{ex.Message}");
            }

            await trayWindow.ShowAsync().AsTask();
            // 兜底确认（各平台统一的第二拍）：显示后显式设最大化。目标态幂等
            //——预置已生效时这里是原生层 no-op，无需任何跳过守卫；延迟一拍沿用
            // 既有节奏（亚秒级等待不接监督器取消令牌）。
            if (TrayRecallMaximize.ShouldEnsure(sample))
            {
                await Task.Delay(300);
                trayWindow.SetMaximized(true);
                // 兜底拍留痕：预置拍已打日志，此拍若不落痕，「两拍只走了一拍」无从判别
                _log?.Invoke("[tray] 唤回：显示后兜底确认已发");
            }
        }
        finally
        {
            Volatile.Write(ref _maximizedAtHide, -1);
        }
    }

    /// <summary>launcher 二次启动唤回：显示主窗并无条件清样本（与托盘唤回同一消费契约——
    /// 残留会让下一次托盘点击把用户手动还原的窗口误最大化）。窗口未就绪时静默忽略。</summary>
    public async Task ActivateFromLauncherAsync()
    {
        CurrentWindowAccessor? accessor = _accessor();
        if (accessor is null)
        {
            return;
        }

        try
        {
            await accessor.Current.ShowAsync();
            _log?.Invoke("[host] launcher 激活：显示主窗完成");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[host] launcher 激活显示主窗失败：{ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _maximizedAtHide, -1);
        }
    }

    /// <summary>语言切换后重建菜单；失败可容忍——保留旧菜单（文案为上一语言），托盘功能不受损。</summary>
    private void RebuildMenu()
    {
        try
        {
            _tray?.SetMenu(TrayMenuActions.BuildItems(includeUpdateItem: _updateMachine is not null, _uiLocale));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[host] 托盘菜单重建失败（保留旧菜单）：{ex.Message}");
        }
    }
}
