using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 进程内失败面模态窗口（ADR 2026-10-23-avalonia-failure-dialog）：新宿主不再经
/// <c>zenity</c>/<c>kdialog</c>/<c>osascript</c> 外挂工具弹窗——那套通道随 Ryn 插件退役，
/// 且「工具不在 PATH 就退回页面」的分支在进程内模态下不存在。
/// 只负责画一屏：标题由窗口标题承担，正文是可选中滚动的只读文本（诊断正文要能被复制），
/// 两个按钮的语义由调用方给定（退出＝true，留在窗口/false 也是关闭框的缺省）。
/// 模态本身是同步原语（<see cref="Window.ShowDialog{TResult}"/>），调用线程不由本类负责。
/// </summary>
internal static class FailureDialogWindow
{
    /// <summary>展示模态并等待用户处置。</summary>
    /// <param name="owner">属主窗口（必须已显示；隐藏/已关闭时交由调用方按通道故障处理）。</param>
    /// <param name="title">窗口标题。</param>
    /// <param name="message">正文（失败原因 + stderr 尾部 + 动作说明）。</param>
    /// <param name="quitLabel">退出按钮文案。</param>
    /// <param name="stayLabel">留在窗口按钮文案（也是 Esc/关闭框的缺省）。</param>
    /// <returns>true = 用户选择退出；false = 留在窗口或直接关闭。</returns>
    public static async Task<bool> ShowAsync(
        Window owner, string title, string message, string quitLabel, string stayLabel)
    {
        var body = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var stay = new Button { Content = stayLabel, IsCancel = true, MinWidth = 96 };
        var quit = new Button { Content = quitLabel, IsDefault = true, MinWidth = 96 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { stay, quit },
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(20),
            RowSpacing = 16,
            Children = { scroll, buttons },
        };
        Grid.SetRow(buttons, 1);

        var window = new Window
        {
            Title = title,
            Width = 560,
            Height = 340,
            MinWidth = 420,
            MinHeight = 240,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout,
        };

        stay.Click += (_, _) => window.Close(false);
        quit.Click += (_, _) => window.Close(true);

        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }
}
