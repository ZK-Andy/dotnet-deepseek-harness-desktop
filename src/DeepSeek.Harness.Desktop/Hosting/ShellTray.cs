using Avalonia;
using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 系统托盘（Avalonia <see cref="TrayIcon"/>）：图标 + 菜单（显示主窗 / 检查更新 / 重启 / 退出）。
/// 只由 <see cref="TryCreate"/> 创建：图标缺失或构造失败即返回 null，关窗随之直接退出——托盘是
/// 增强能力，绝不挡退出。菜单内容取旧栈同一模型 <see cref="TrayMenuActions"/>（文案与位次的单一家，
/// 两宿主共用），本类只把条目 id 映射成原生回调；语言切换后的菜单重建尚未接（locale 命令已在 G2 装配，
/// 但改语言不会重建本菜单，缺口记在 ADR avalonia-g3-exit-restart 的残项）。
/// 「检查更新」项与自更新栈同生灭（未装载即不出现，与旧宿主同判据）；检查结论落页面横幅
/// （ADR avalonia-g3-update-surface）——Avalonia 托盘没有系统通知面，旧宿主的通知通道随迁移下线。
/// 菜单动作都是原生回调，不经页面事件中继（托盘没有页面侧事件源）。</summary>
internal sealed class ShellTray : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly TrayIcons _icons;
    private readonly Action _recall;
    private readonly Action _quit;

    private ShellTray(TrayIcon icon, TrayIcons icons, Action recall, Action quit, Action<string> log)
    {
        _icon = icon;
        _icons = icons;
        _recall = recall;
        _quit = quit;
    }

    /// <summary>尝试挂托盘。返回 null = 本机不可用（图标缺失、无 StatusNotifierItem、缺原生库），
    /// 调用方据此让关窗直接退出。</summary>
    /// <param name="iconPath">托盘图标文件（可用性由装配层探测后传入）。</param>
    /// <param name="iconAvailable">图标文件是否存在（装配层探测，免本类直调文件系统）。</param>
    /// <param name="uiLocale">UI 语言单点（决定创建时的菜单文案；切换重建随 G3）。</param>
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
        var icons = new TrayIcons();
        try
        {
            created = new TrayIcon
            {
                Icon = new WindowIcon(iconPath),
                ToolTipText = "DeepSeek Harness Desktop",
                Menu = BuildMenu(uiLocale, recall, checkUpdate, restart, quit),
                IsVisible = true,
            };
            created.Clicked += (_, _) => recall();
            icons.Add(created);
            TrayIcon.SetIcons(Application.Current, icons);
            return new ShellTray(created, icons, recall, quit, log);
        }
        catch (Exception ex)
        {
            // 无托盘环境（无 StatusNotifierItem、缺原生库）：只留痕不挡启动；半成品就地释放
            created?.Dispose();
            icons.Clear();
            log($"[host] 系统托盘不可用，关窗直接退出：{ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
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
    /// 菜单模型与动作表由同一份条目清单生成，走到未知分支说明两处已漂移。抛出点在构建期
    /// （<see cref="BuildMenu"/> 内、<see cref="TryCreate"/> 的 try 里），结果是整块托盘不注册、
    /// 关窗直退并留一行日志，不会在点击回调里崩。</summary>
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
