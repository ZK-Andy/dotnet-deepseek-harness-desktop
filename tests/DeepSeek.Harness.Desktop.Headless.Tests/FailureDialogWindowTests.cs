using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeepSeek.Harness.Desktop.Hosting;
using Xunit;

namespace DeepSeek.Harness.Desktop.Headless.Tests;

/// <summary>V2：进程内失败面模态（ADR 2026-10-23-avalonia-failure-dialog）的 headless 断言。
/// 该窗口是壳自有 Avalonia UI 中此前唯一无机器覆盖的面——ShowDialog 模态生命周期、按钮语义
/// （退出 = true）、Esc/关闭框的 IsCancel 缺省（false）。恢复页与 WebView2 探测分支已由
/// 端口化单测覆盖（RecoveryPageBuilderTests/WebView2StartupGuardTests），不在此重复。</summary>
public class FailureDialogWindowTests
{
    /// <summary>点「退出」按钮返回 true，模态随之关闭。</summary>
    [AvaloniaFact]
    public async Task QuitButton_ResolvesTrue()
    {
        Window owner = CreateShownOwner();
        Task<bool> dialogTask = FailureDialogWindow.ShowAsync(
            owner, "标题", "正文", quitLabel: "退出", stayLabel: "留在窗口");
        Dispatcher.UIThread.RunJobs();

        Window dialog = Assert.Single(owner.OwnedWindows);
        ClickButton(dialog, "退出");
        Dispatcher.UIThread.RunJobs();

        Assert.True(await dialogTask);
        Assert.False(dialog.IsVisible);
    }

    /// <summary>点「留在窗口」按钮返回 false。</summary>
    [AvaloniaFact]
    public async Task StayButton_ResolvesFalse()
    {
        Window owner = CreateShownOwner();
        Task<bool> dialogTask = FailureDialogWindow.ShowAsync(
            owner, "标题", "正文", quitLabel: "退出", stayLabel: "留在窗口");
        Dispatcher.UIThread.RunJobs();

        Window dialog = Assert.Single(owner.OwnedWindows);
        ClickButton(dialog, "留在窗口");
        Dispatcher.UIThread.RunJobs();

        Assert.False(await dialogTask);
    }

    /// <summary>Esc 触发 IsCancel 按钮的缺省语义：关闭模态并返回 false。</summary>
    [AvaloniaFact]
    public async Task Escape_UsesCancelDefault_ResolvesFalse()
    {
        Window owner = CreateShownOwner();
        Task<bool> dialogTask = FailureDialogWindow.ShowAsync(
            owner, "标题", "正文", quitLabel: "退出", stayLabel: "留在窗口");
        Dispatcher.UIThread.RunJobs();

        Window dialog = Assert.Single(owner.OwnedWindows);
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(await dialogTask);
    }

    private static Window CreateShownOwner()
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }

    /// <summary>按内容文案找到按钮并经输入管道点击（走视觉树命中，不是直调 Click 事件）。</summary>
    private static void ClickButton(Window window, string content)
    {
        Button button = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == content);
        Point? center = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(center);
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
