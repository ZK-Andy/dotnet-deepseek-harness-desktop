using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 系统托盘（Avalonia <see cref="TrayIcon"/>）：图标 + 菜单（显示主窗 / 检查更新 / 重启 / 退出）。
/// 只由 <see cref="TryCreate"/> 创建：图标缺失或构造失败即返回 null，关窗随之直接退出——托盘是
/// 增强能力，绝不挡退出。菜单内容取旧栈同一模型 <see cref="TrayMenuActions"/>（文案与位次的单一家，
/// 两宿主共用），本类只把条目 id 映射成原生回调。菜单随 UI 语言重建：<see cref="UiLocale.Changed"/>
/// 经 <see cref="TrayMenuRebuild"/> 触发后，在 UI 线程按当前语言重造并换装（ADR tray-menu-locale-rebuild）；
/// 托盘不可用时不建绑定。
/// 「检查更新」项与自更新栈同生灭（未装载即不出现，与旧宿主同判据）；检查结论落页面横幅
/// （ADR avalonia-g3-update-surface）——Avalonia 托盘没有系统通知面，旧宿主的通知通道随迁移下线。
/// 菜单动作都是原生回调，不经页面事件中继（托盘没有页面侧事件源）。</summary>
internal sealed class ShellTray : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly TrayIcons _icons;
    private readonly UiLocale _uiLocale;
    private readonly Action _recall;
    private readonly Action? _checkUpdate;
    private readonly Action _restart;
    private readonly Action _quit;
    private readonly Action<string> _log;
    private readonly TrayMenuRebuild _menuRebuild;
    private int _disposed;

    private ShellTray(
        TrayIcon icon,
        TrayIcons icons,
        UiLocale uiLocale,
        Action recall,
        Action? checkUpdate,
        Action restart,
        Action quit,
        Action<string> log)
    {
        _icon = icon;
        _icons = icons;
        _uiLocale = uiLocale;
        _recall = recall;
        _checkUpdate = checkUpdate;
        _restart = restart;
        _quit = quit;
        _log = log;
        _menuRebuild = new TrayMenuRebuild(uiLocale, ScheduleMenuRebuild);
    }

    /// <summary>尝试挂托盘。返回 null = 本机不可用（图标缺失、无 StatusNotifierItem、缺原生库），
    /// 调用方据此让关窗直接退出。</summary>
    /// <param name="iconPath">托盘图标文件（可用性由装配层探测后传入）。</param>
    /// <param name="iconAvailable">图标文件是否存在（装配层探测，免本类直调文件系统）。</param>
    /// <param name="uiLocale">UI 语言单点（创建时的菜单文案与后续重建的事件源）。</param>
    /// <param name="recall">唤回主窗动作（经唤回单点）。</param>
    /// <param name="checkUpdate">检查更新动作；null = 自更新栈未装载（菜单项随之消失）。</param>
    /// <param name="restart">重启动作（退出管道：回收运行时后拉起新实例）。</param>
    /// <param name="quit">退出动作（批准关窗并结束生命周期）。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>挂好的托盘；null = 不可用。</returns>
    public static ShellTray? TryCreate(string iconPath, bool iconAvailable, UiLocale uiLocale, Action recall, Action? checkUpdate, Action restart, Action quit, Action<string> log)
    {
        if (!iconAvailable || Application.Current is null || !PlatformHasTrayHost())
        {
            log("[host] 托盘不可用（图标缺失/无 Application/无会话总线）：托盘不注册，关窗直接退出");
            return null;
        }

        TrayIcon? created = null;
        ShellTray? tray = null;
        var icons = new TrayIcons();
        try
        {
            created = new TrayIcon
            {
                Icon = new WindowIcon(iconPath),
                ToolTipText = "DeepSeek Harness Desktop",
                IsVisible = true,
            };
            created.Clicked += (_, _) => recall();
            tray = new ShellTray(created, icons, uiLocale, recall, checkUpdate, restart, quit, log);
            created.Menu = tray.BuildCurrentMenu();
            icons.Add(created);
            TrayIcon.SetIcons(Application.Current, icons);
            return tray;
        }
        catch (Exception ex)
        {
            // 无托盘环境（无 StatusNotifierItem、缺原生库）：只留痕不挡启动；半成品就地释放。
            // 已构造的 ShellTray 必须先 Dispose——它已订阅进程级 UiLocale，单释放图标会把订阅漏到进程结束。
            if (tray is not null)
            {
                tray.Dispose();
            }
            else
            {
                created?.Dispose();
            }

            icons.Clear();
            log($"[host] 系统托盘不可用，关窗直接退出：{ex.Message}");
            return null;
        }
    }

    /// <summary>语言变化后的重建（ADR tray-menu-locale-rebuild）：事件来自页面桥线程，构建与换装都在
    /// UI 线程完成——原生菜单只能在该线程替换，构建异常也不落回 <see cref="UiLocale.Changed"/> 的调用方。
    /// 托盘已释放时投递体直接丢弃；失败留痕并保留旧菜单。</summary>
    private void ScheduleMenuRebuild() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _icon.Menu = BuildCurrentMenu();
            }
            catch (Exception ex)
            {
                _log($"[host] 托盘菜单重建失败：{ex.Message}");
            }
        });

    /// <summary>按当前语言构造菜单：首建与重建共用的唯一入口。</summary>
    /// <returns>原生菜单。</returns>
    private NativeMenu BuildCurrentMenu() => BuildMenu(_uiLocale, _recall, _checkUpdate, _restart, _quit);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _menuRebuild.Dispose();
        try
        {
            _icon.IsVisible = false;
            _icons.Remove(_icon);
        }
        catch (Exception)
        {
            // 生命周期收尾：托盘随进程退出消失，取消失败无需报告（关窗链路不因它中断）
        }

        _icon.Dispose();
    }

    /// <summary>平台是否具备托盘宿主：Windows/macOS 恒有；Linux 需会话总线（StatusNotifierItem 走 D-Bus）。
    /// 这是前置判据而非可用性证明——Avalonia 在缺实现时可能静默建出不可见图标（ADR 记了该边界，
    /// 二启唤回是隐藏后的恢复通道）。</summary>
    private static bool PlatformHasTrayHost() =>
        OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        || (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")));

    /// <summary>菜单构造：内容（文案/位次/是否含检查更新）取 <see cref="TrayMenuActions.BuildEntries"/>，
    /// 本类只按条目 id 派发到原生回调。</summary>
    /// <param name="uiLocale">UI 语言单点。</param>
    /// <param name="recall">唤回主窗。</param>
    /// <param name="checkUpdate">检查更新；null = 未装载。</param>
    /// <param name="restart">重启。</param>
    /// <param name="quit">退出。</param>
    /// <returns>原生菜单。</returns>
    internal static NativeMenu BuildMenu(UiLocale uiLocale, Action recall, Action? checkUpdate, Action restart, Action quit)
    {
        var menu = new NativeMenu();
        foreach (TrayMenuEntry item in TrayMenuActions.BuildEntries(checkUpdate is not null, uiLocale))
        {
            if (item.Separator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            menu.Add(Item(item.Label, ResolveAction(item.Id, recall, checkUpdate, restart, quit)));
        }

        return menu;
    }

    /// <summary>条目 id → 原生动作（纯映射，可单测）：未知 id 视为装配错误直接抛出——
    /// 菜单模型与动作表由同一份条目清单生成，走到未知分支说明两处已漂移。首建时抛出点在
    /// <see cref="TryCreate"/> 的 try 里（整块托盘不注册、关窗直退并留一行日志）；运行期重建时
    /// 抛出被 <see cref="ScheduleMenuRebuild"/> 的 catch 收成日志并保留旧菜单，不会在点击回调里崩。</summary>
    /// <param name="id">条目 id（<see cref="TrayMenuActions"/> 的常量）。</param>
    /// <param name="recall">唤回主窗。</param>
    /// <param name="checkUpdate">检查更新；null = 未装载（该条目不应出现）。</param>
    /// <param name="restart">重启。</param>
    /// <param name="quit">退出。</param>
    /// <returns>该条目的动作。</returns>
    internal static Action ResolveAction(string id, Action recall, Action? checkUpdate, Action restart, Action quit) => id switch
    {
        TrayMenuActions.ShowItemId => recall,
        TrayMenuActions.CheckUpdateItemId => checkUpdate
            ?? throw new InvalidOperationException("检查更新条目在未装载更新栈时出现"),
        TrayMenuActions.RestartItemId => restart,
        TrayMenuActions.QuitItemId => quit,
        _ => throw new InvalidOperationException($"未知托盘条目 id：{id}"),
    };

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
