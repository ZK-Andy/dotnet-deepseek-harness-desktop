namespace DeepSeek.Harness.Desktop.Core.Platform;

/// <summary>主窗窗口态（几何持久化的封闭集；minimized 不落盘，还原时按 <see cref="Normal"/> 处理）。</summary>
public enum WindowGeometryState
{
    /// <summary>常态（缺省还原目标）。</summary>
    Normal,

    /// <summary>最大化。</summary>
    Maximized,

    /// <summary>全屏。</summary>
    FullScreen,
}

/// <summary>主窗几何快照：位置、尺寸与窗口态（ADR 2026-10-25-window-identity-geometry-teardown）。</summary>
/// <param name="X">窗口左上角 X（像素）。</param>
/// <param name="Y">窗口左上角 Y（像素）。</param>
/// <param name="Width">宽度（DIP）。</param>
/// <param name="Height">高度（DIP）。</param>
/// <param name="State">窗口态封闭集。</param>
public sealed record WindowGeometry(double X, double Y, double Width, double Height, WindowGeometryState State);
