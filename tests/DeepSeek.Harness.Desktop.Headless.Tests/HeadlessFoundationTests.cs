using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace DeepSeek.Harness.Desktop.Headless.Tests;

/// <summary>V1 地基：headless 平台可用的机械化证明——窗口构造与布局、鼠标输入经视觉树
/// 路由到控件、Skia 离屏帧捕获。全程无需显示服务器，三平台 CI 可跑；
/// 后续失败面/对话框/顶栏端口用例以本组为前提。</summary>
public class HeadlessFoundationTests
{
    /// <summary>窗口构造、显示后布局非零，且 Skia 离屏帧可捕获（地基：后续用例的帧断言前提）。</summary>
    [AvaloniaFact]
    public void WindowConstructs_Shows_AndCapturesFrame()
    {
        var window = new Window { Width = 300, Height = 200, Content = new Border() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Bounds.Width > 0, $"window bounds = {window.Bounds}");

        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
    }

    /// <summary>鼠标按下/抬起经 headless 输入管道与视觉树命中路由到控件。</summary>
    [AvaloniaFact]
    public void MouseInputRoutesToVisualTreeControl()
    {
        int pressed = 0;
        var panel = new Border { Background = Brushes.Red };
        panel.PointerPressed += (_, _) => pressed++;

        var window = new Window { Width = 300, Height = 200, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(new Point(150, 100), MouseButton.Left);
        window.MouseUp(new Point(150, 100), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, pressed);
    }
}
