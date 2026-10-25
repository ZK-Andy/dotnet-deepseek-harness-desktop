using Avalonia;
using Avalonia.Controls;
using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 窗口几何的采集与还原（ADR 2026-10-25-window-identity-geometry-teardown）：纯映射，headless 窗口可测。
/// 采集点=窗口态变化/隐藏到托盘/真实关窗；还原点=主窗构造。位置尺寸只在还原常态时有意义，
/// 最大化/全屏由窗口系统接管几何——但快照仍全量保存（先还原尺寸再置态，取消最大化时尺寸不漂）。
/// </summary>
internal static class WindowGeometryRestore
{
    /// <summary>尺寸还原的合理性下限：低于此值视为损坏快照，跳过尺寸部分（防不可见窗口）。</summary>
    private const double MinWidth = 200;

    /// <summary>同上（高度下限）。</summary>
    private const double MinHeight = 150;

    /// <summary>从窗口采集几何快照；minimized 按 normal 落盘（还原最小化无意义）。</summary>
    /// <param name="window">源窗口。</param>
    /// <returns>几何快照。</returns>
    public static WindowGeometry Capture(Window window) =>
        new(window.Position.X, window.Position.Y, window.Width, window.Height, StateName(window.WindowState));

    /// <summary>把快照还原到窗口：先位置尺寸（常态值），后窗口态。损坏字段跳过对应部分，绝不抛出。
    /// (0,0) 位置视为无效快照（卡死脱同步期的产物：Avalonia 记的位置与窗口系统实际摆放脱节）——
    /// 跳过并保留 <see cref="WindowStartupLocation.CenterScreen"/> 居中启动，不回放左上角。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="geometry">存储快照。</param>
    public static void Apply(Window window, WindowGeometry geometry)
    {
        if ((geometry.X != 0 || geometry.Y != 0)
            && double.IsFinite(geometry.X) && double.IsFinite(geometry.Y))
        {
            window.Position = new PixelPoint((int)Math.Round(geometry.X), (int)Math.Round(geometry.Y));
            // 存档位置要生效必须切 Manual，否则 CenterScreen 在显示时覆盖刚落的位置
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }

        if (double.IsFinite(geometry.Width) && geometry.Width >= MinWidth
            && double.IsFinite(geometry.Height) && geometry.Height >= MinHeight)
        {
            window.Width = geometry.Width;
            window.Height = geometry.Height;
        }

        window.WindowState = geometry.State switch
        {
            WindowGeometryState.Maximized => WindowState.Maximized,
            WindowGeometryState.FullScreen => WindowState.FullScreen,
            _ => WindowState.Normal,
        };
    }

    /// <summary>窗口态 → 存储态；minimized 归 <see cref="WindowGeometryState.Normal"/>。</summary>
    /// <param name="state">当前窗口态。</param>
    /// <returns>存储态。</returns>
    private static WindowGeometryState StateName(WindowState state) => state switch
    {
        WindowState.Maximized => WindowGeometryState.Maximized,
        WindowState.FullScreen => WindowGeometryState.FullScreen,
        _ => WindowGeometryState.Normal,
    };
}
