namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 托盘唤回的最大化动作判据（纯函数可单测）：仅当隐藏前确认为最大化、且原生镜像
/// （<c>IRynWindow.IsMaximized</c>）当前报告非最大化时才需要发出最大化。两个使用点
/// 共用此判据——Linux 隐藏态预置（map 前发出，消除首唤两段式闪变）与唤回后的
/// 单次补正兜底。隐藏前未知（采样异常）绝不动作——行为退回无补正的基线。
/// </summary>
public static class TrayRecallMaximize
{
    /// <summary>是否需要发出 <c>ToggleMaximize</c>。<paramref name="maximizedAtHide"/> 取 1=隐藏前最大化 / 0=非最大化 / -1=未知。</summary>
    public static bool NeedsMaximize(int maximizedAtHide, bool isNowMaximized) =>
        maximizedAtHide == 1 && !isNowMaximized;
}
