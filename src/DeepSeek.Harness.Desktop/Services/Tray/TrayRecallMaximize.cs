namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 托盘唤回的最大化补正判定（纯函数可单测）：仅当隐藏前确认为最大化、且唤回后
/// 原生查询（<c>IRynWindow.IsMaximized</c>）报告非最大化时才补一次 toggle。
/// 隐藏前未知（采样异常）绝不动作——行为退回无补正的基线。
/// </summary>
public static class TrayRecallMaximize
{
    /// <summary>是否需要补一次 <c>ToggleMaximize</c>。<paramref name="maximizedAtHide"/> 取 1=隐藏前最大化 / 0=非最大化 / -1=未知。</summary>
    public static bool ShouldRestore(int maximizedAtHide, bool isNowMaximized) =>
        maximizedAtHide == 1 && !isNowMaximized;
}
