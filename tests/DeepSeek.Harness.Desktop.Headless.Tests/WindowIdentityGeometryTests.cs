using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DeepSeek.Harness.Desktop.Core.Localization;
using DeepSeek.Harness.Desktop.Core.Platform;
using DeepSeek.Harness.Desktop.Hosting;
using DeepSeek.Harness.Desktop.PageBridge;
using Xunit;

namespace DeepSeek.Harness.Desktop.Headless.Tests;

/// <summary>窗口身份与几何修复批（ADR 2026-10-25-window-identity-geometry-teardown）：
/// WM_CLASS 载荷形状、几何采集/还原、窗口图标装配与退出摘除。窗口用真件（headless 平台）。</summary>
public class WindowIdentityGeometryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dsh-widentity-{Guid.NewGuid():N}");

    /// <summary>WM_CLASS 载荷 = 两个 null 结尾字符串拼接（instance、class、末字节终止符）。</summary>
    [Fact]
    public void BuildPayload_ConcatenatesTwoNullTerminatedStrings()
    {
        byte[] payload = X11WindowIdentity.BuildPayload("Inst", "Class");

        Assert.Equal("Inst\0Class\0", Encoding.ASCII.GetString(payload));
    }

    /// <summary>WM_CLASS 常量与打包面值同源（跨语言契约的测试侧钉）。</summary>
    [Fact]
    public void WmClass_IsPackagingValue()
    {
        Assert.Equal("Dotnet.Deepseek.Harness.Desktop", X11WindowIdentity.WmClass);
    }

    /// <summary>Apply 还原位置/尺寸/窗口态，并切 Manual（否则 CenterScreen 在显示时覆盖位置）。</summary>
    [AvaloniaFact]
    public void Apply_RestoresPositionSizeAndState()
    {
        var window = new Window { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var geometry = new WindowGeometry(88, 66, 1024, 768, WindowGeometryState.Maximized);

        WindowGeometryRestore.Apply(window, geometry);

        Assert.Equal(new PixelPoint(88, 66), window.Position);
        Assert.Equal(1024, window.Width);
        Assert.Equal(768, window.Height);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
    }

    /// <summary>(0,0) 快照按无效处理：跳过位置并保留居中启动（脱同步产物不回放左上角）。</summary>
    [AvaloniaFact]
    public void Apply_ZeroOrigin_SkipsPositionAndKeepsCenterScreen()
    {
        var window = new Window { WindowStartupLocation = WindowStartupLocation.CenterScreen };

        WindowGeometryRestore.Apply(window, new WindowGeometry(0, 0, 1200, 800, WindowGeometryState.Normal));

        Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(1200, window.Width);
    }

    /// <summary>全屏态还原。</summary>
    [AvaloniaFact]
    public void Apply_RestoresFullScreen()
    {
        var window = new Window();

        WindowGeometryRestore.Apply(window, new WindowGeometry(0, 0, 1200, 800, WindowGeometryState.FullScreen));

        Assert.Equal(WindowState.FullScreen, window.WindowState);
    }

    /// <summary>损坏尺寸（过小）跳过尺寸部分，位置与态照常还原。</summary>
    [AvaloniaFact]
    public void Apply_TinySize_SkipsSizeOnly()
    {
        var window = new Window { Width = 1200, Height = 800 };

        WindowGeometryRestore.Apply(window, new WindowGeometry(10, 20, 50, 40, WindowGeometryState.Normal));

        Assert.Equal(new PixelPoint(10, 20), window.Position);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(1200, window.Width);
        Assert.Equal(800, window.Height);
    }

    /// <summary>采集→还原 round-trip：全屏窗口采集后还原到新窗口保持全屏。</summary>
    [AvaloniaFact]
    public void CaptureThenApply_RoundTrips()
    {
        var source = new Window { Position = new PixelPoint(12, 34), Width = 1111, Height = 640, WindowState = WindowState.FullScreen };

        WindowGeometry geometry = WindowGeometryRestore.Capture(source);
        var target = new Window();
        WindowGeometryRestore.Apply(target, geometry);

        Assert.Equal(new PixelPoint(12, 34), target.Position);
        Assert.Equal(1111, target.Width);
        Assert.Equal(640, target.Height);
        Assert.Equal(WindowState.FullScreen, target.WindowState);
    }

    /// <summary>minimized 采集归 normal（还原最小化无意义）。</summary>
    [AvaloniaFact]
    public void Capture_MinimizedStoredAsNormal()
    {
        var window = new Window { WindowState = WindowState.Minimized };

        Assert.Equal(WindowGeometryState.Normal, WindowGeometryRestore.Capture(window).State);
    }

    /// <summary>主窗构造带图标路径 → <c>Window.Icon</c> 已装配（X11 _NET_WM_ICON 数据源）。</summary>
    [AvaloniaFact]
    public void ShellWindow_WithIconPath_SetsWindowIcon()
    {
        string iconPath = WriteIcon();
        ShellWindow window = CreateWindow(iconPath);

        Assert.NotNull(window.Icon);
    }

    /// <summary>无图标路径 → 不设图标（保持默认行为）。</summary>
    [AvaloniaFact]
    public void ShellWindow_WithoutIconPath_LeavesIconNull()
    {
        ShellWindow window = CreateWindow(null);

        Assert.Null(window.Icon);
    }

    /// <summary>退出摘除：WebView 从视觉树摘下（收尾期防在途绘制撞 EGL 销毁）。</summary>
    [AvaloniaFact]
    public void ShellWindow_DetachForShutdown_ClearsContent()
    {
        ShellWindow window = CreateWindow(null);
        Assert.NotNull(window.Content);

        window.DetachContentForShutdown();

        Assert.Null(window.Content);
    }

    /// <summary>HideToTray 落几何快照（冷启动还原的数据源）。</summary>
    [AvaloniaFact]
    public void ShellWindow_HideToTray_SavesGeometry()
    {
        RecordingStore store = new();
        ShellWindow window = CreateWindow(null, store);
        window.Position = new PixelPoint(21, 43);
        window.Width = 1100;

        window.HideToTray();

        Assert.NotNull(store.Last);
        Assert.Equal(new PixelPoint(21, 43), new PixelPoint((int)store.Last.X, (int)store.Last.Y));
        Assert.Equal(1100, store.Last.Width);
    }

    /// <summary>构造时从存储还原几何（冷启动回上次尺寸/态）。</summary>
    [AvaloniaFact]
    public void ShellWindow_ConstructWithStore_RestoresGeometry()
    {
        RecordingStore store = new()
        {
            Last = new WindowGeometry(31, 42, 1330, 880, WindowGeometryState.Normal),
        };

        ShellWindow window = CreateWindow(null, store);

        Assert.Equal(new PixelPoint(31, 42), window.Position);
        Assert.Equal(1330, window.Width);
        Assert.Equal(880, window.Height);
    }

    private ShellWindow CreateWindow(string? iconPath, IWindowGeometryStore? store = null)
    {
        ShellBridgeHost bridgeHost = PageBridgeRegistration.CreateShellBridgeHost(
            null, new UiLocale(), macChrome: false, captionHeightPx: 0, _ => { });
        return new ShellWindow(null, devTools: false, bridgeHost, iconPath, store);
    }

    private string WriteIcon()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "icon.png");
        // 1x1 PNG（标准最小图），WindowIcon 解码用
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        File.WriteAllBytes(path, Convert.FromBase64String(png));
        return path;
    }

    /// <summary>清理临时目录（图标夹具）。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>记序 fake 存储（采集断言用）。</summary>
    private sealed class RecordingStore : IWindowGeometryStore
    {
        public WindowGeometry? Last { get; set; }

        public WindowGeometry? Load() => Last;

        public void Save(WindowGeometry geometry) => Last = geometry;
    }
}
