using Avalonia;
using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 系统托盘（Avalonia <see cref="TrayIcon"/>）：图标 + 菜单（显示主窗 / 退出）。
/// 只由 <see cref="TryCreate"/> 创建：图标缺失或构造失败即返回 null，关窗随之直接退出——托盘是
/// 增强能力，绝不挡退出。文案取自 <see cref="UiCopy"/>，语言切换重建随 G3 的 locale 命令路由。
/// 重启与检查更新两项随 G3（退出管道与更新 UI 未接入新宿主），本片菜单不出现。</summary>
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
    /// <param name="quit">退出动作（批准关窗并结束生命周期）。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>挂好的托盘；null = 不可用。</returns>
    public static ShellTray? TryCreate(string iconPath, bool iconAvailable, UiLocale uiLocale, Action recall, Action quit, Action<string> log)
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
                Menu = BuildMenu(uiLocale, recall, quit),
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

    private static NativeMenu BuildMenu(UiLocale uiLocale, Action recall, Action quit)
    {
        bool english = uiLocale.IsEnglish;
        var menu = new NativeMenu();
        menu.Add(Item(UiCopy.TrayShowMainWindow(english), recall));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(UiCopy.TrayQuit(english), quit));
        return menu;
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
