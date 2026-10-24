using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Hosting;
using DeepSeek.Harness.Desktop.PageBridge;
using Xunit;

namespace DeepSeek.Harness.Desktop.Headless.Tests;

/// <summary>V3：无边框顶栏拖拽/缩放的端口调用断言（ADR 2026-10-24-ci-desktop-acceptance-via-headless）。
/// 全链真件：页面命令帧 → <see cref="WindowCommandRouter"/> → <see cref="AvaloniaWindowCommands"/>
/// （含 CSS→设备像素换算）→ 端口；只有平台端口 <see cref="IWindowDrag"/> 用假件记录调用。
/// 窗口系统接管的拖拽最终结果（窗口真的移动）观测不到，按残留代价记录，不在此断言。</summary>
public class WindowDragPortTests
{
    /// <summary>拖拽命令经换算到达端口：CSS 像素坐标按 RenderScaling 换算成设备像素。</summary>
    [AvaloniaFact]
    public async Task DragCommand_InvokesTryBeginMove_WithDevicePixels()
    {
        RecordingDrag drag = new();
        (Window window, WindowCommandRouter router) = CreateWiredWindow(drag);
        window.SetRenderScaling(2.0);

        string result = await router.RouteAsync(
            HostCommandIds.WindowStartDrag, Json("{\"x\":101.7,\"y\":49.2}"), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("{}", result);
        Assert.Equal(1, drag.MoveCount);
        Assert.Equal(new PixelPoint(204, 98), drag.LastMovePoint);
    }

    /// <summary>缩放命令把边名解成强类型边，坐标同路换算。</summary>
    [AvaloniaFact]
    public async Task ResizeCommand_InvokesTryBeginResize_WithTypedEdge()
    {
        RecordingDrag drag = new();
        (Window window, WindowCommandRouter router) = CreateWiredWindow(drag);
        window.SetRenderScaling(1.0);

        string result = await router.RouteAsync(
            HostCommandIds.WindowStartResize, Json("{\"edge\":\"top-left\",\"x\":10,\"y\":20}"), CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("{}", result);
        Assert.Equal(1, drag.ResizeCount);
        Assert.Equal(WindowEdgeKind.TopLeft, drag.LastEdge);
        Assert.Equal(new PixelPoint(10, 20), drag.LastResizePoint);
    }

    /// <summary>参数帧非法回错误帧，端口一次都不被调（错误处理不惊动平台互操作）。</summary>
    [AvaloniaFact]
    public async Task InvalidDragArgs_ReturnErrorFrame_WithoutPortCall()
    {
        RecordingDrag drag = new();
        (Window _, WindowCommandRouter router) = CreateWiredWindow(drag);

        string result = await router.RouteAsync(
            HostCommandIds.WindowStartDrag, Json("{\"y\":50}"), CancellationToken.None);

        Assert.Contains("error", result);
        Assert.Equal(0, drag.MoveCount);
    }

    /// <summary>端口拒绝（如无可用通道）时命令仍回成功帧——发起失败由端口留痕，页面不等待。</summary>
    [AvaloniaFact]
    public async Task PortRejection_StillReturnsSuccessFrame()
    {
        RecordingDrag drag = new() { AllowMove = false };
        (Window _, WindowCommandRouter router) = CreateWiredWindow(drag);

        string result = await router.RouteAsync(
            HostCommandIds.WindowStartDrag, Json("{\"x\":1,\"y\":2}"), CancellationToken.None);

        Assert.Equal("{}", result);
        Assert.Equal(1, drag.MoveCount);
    }

    private static (Window Window, WindowCommandRouter Router) CreateWiredWindow(IWindowDrag drag)
    {
        var window = new Window { Width = 300, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var router = new WindowCommandRouter(new AvaloniaWindowCommands(window, drag, _ => { }));
        return (window, router);
    }

    private static ReadOnlyMemory<byte> Json(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    /// <summary>记录调用与参数的假拖拽端口。</summary>
    private sealed class RecordingDrag : IWindowDrag
    {
        /// <summary>是否放行移动（缺省放行；false 模拟端口拒绝）。</summary>
        public bool AllowMove { get; init; } = true;

        /// <summary>移动调用次数。</summary>
        public int MoveCount { get; private set; }

        /// <summary>缩放调用次数。</summary>
        public int ResizeCount { get; private set; }

        /// <summary>最近一次移动的设备像素坐标。</summary>
        public PixelPoint LastMovePoint { get; private set; }

        /// <summary>最近一次缩放的边。</summary>
        public WindowEdgeKind LastEdge { get; private set; }

        /// <summary>最近一次缩放的设备像素坐标。</summary>
        public PixelPoint LastResizePoint { get; private set; }

        /// <inheritdoc />
        public string Name => "test";

        /// <inheritdoc />
        public bool TryBeginMove(Window window, PixelPoint screenPoint)
        {
            MoveCount++;
            LastMovePoint = screenPoint;
            return AllowMove;
        }

        /// <inheritdoc />
        public bool TryBeginResize(Window window, WindowEdgeKind edge, PixelPoint screenPoint)
        {
            ResizeCount++;
            LastEdge = edge;
            LastResizePoint = screenPoint;
            return true;
        }
    }
}
