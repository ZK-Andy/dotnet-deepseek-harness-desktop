using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>新宿主托盘菜单（ADR avalonia-g3-update-surface）：内容取自 <c>TrayMenuActions</c> 同一模型
/// （显示主窗 / 检查更新 / 分隔线 / 重启 / 退出），本类只做「条目 id → 原生回调」映射。
/// 语言切换后的重建不在本组覆盖（缺口记在 avalonia-g3-exit-restart 的残项）。</summary>
public class ShellTrayActionTests
{
    /// <summary>四条菜单 id 各自映射到对应动作（互不串位：检查更新不得落到唤回上）。</summary>
    [Fact]
    public void ResolveAction_MapsEachMenuIdToItsOwnAction()
    {
        int recall = 0;
        int check = 0;
        int restart = 0;
        int quit = 0;
        Action recallAction = () => recall++;
        Action checkAction = () => check++;
        Action restartAction = () => restart++;
        Action quitAction = () => quit++;

        ShellTray.ResolveAction(TrayMenuActions.ShowItemId, recallAction, checkAction, restartAction, quitAction)();
        ShellTray.ResolveAction(TrayMenuActions.CheckUpdateItemId, recallAction, checkAction, restartAction, quitAction)();
        ShellTray.ResolveAction(TrayMenuActions.RestartItemId, recallAction, checkAction, restartAction, quitAction)();
        ShellTray.ResolveAction(TrayMenuActions.QuitItemId, recallAction, checkAction, restartAction, quitAction)();

        Assert.Equal(1, recall);
        Assert.Equal(1, check);
        Assert.Equal(1, restart);
        Assert.Equal(1, quit);
    }

    /// <summary>未知条目 id：装配漂移，构建期直接抛出（不让原生菜单挂上与动作对不上的条目）。</summary>
    [Fact]
    public void ResolveAction_UnknownId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ShellTray.ResolveAction("no-such-item", () => { }, () => { }, () => { }, () => { }));
    }

    /// <summary>菜单位次与文案来自同一模型：装载时五项（含分隔线），未装载时四项；
    /// 每项 Header 必须逐位等于 <c>TrayMenuActions.BuildEntries</c> 的文案（搬运写错或空串即红）。</summary>
    [Fact]
    public void BuildMenu_FollowsSharedMenuModel()
    {
        var zh = new UiLocale();

        NativeMenu withUpdate = ShellTray.BuildMenu(zh, () => { }, () => { }, () => { }, () => { });
        NativeMenu withoutUpdate = ShellTray.BuildMenu(zh, () => { }, null, () => { }, () => { });

        Assert.Equal(5, withUpdate.Items.Count);
        Assert.Equal(4, withoutUpdate.Items.Count);
        // 分隔线是类型（不是空文案项）：Header 序列断言覆盖不到这一层
        Assert.Contains(withUpdate.Items, item => item is NativeMenuItemSeparator);
        Assert.Equal(
            TrayMenuActions.BuildEntries(includeUpdateItem: true, zh).Select(entry => entry.Label),
            Headers(withUpdate));
        Assert.Equal(
            TrayMenuActions.BuildEntries(includeUpdateItem: false, zh).Select(entry => entry.Label),
            Headers(withoutUpdate));
    }

    /// <summary>菜单项的 Header 序列（分隔线没有文案，按空串对齐模型）。</summary>
    private static IEnumerable<string?> Headers(NativeMenu menu) =>
        menu.Items.Select(item => item is NativeMenuItemSeparator ? string.Empty : ((NativeMenuItem)item).Header);
}
