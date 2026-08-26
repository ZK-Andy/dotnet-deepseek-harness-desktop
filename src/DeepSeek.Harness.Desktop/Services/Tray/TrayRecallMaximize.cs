namespace DeepSeek.Harness.Desktop.Services.Tray;

/// <summary>
/// 托盘唤回的最大化动作判据（纯函数可单测）：仅当隐藏前采样确认为最大化时才需要
/// 发出最大化。动作面统一为 <c>IRynWindow.SetMaximized(true)</c>——目标态显式的幂等
/// 原生设置，调用点不读取 <c>IsMaximized</c> 事件镜像（Fedora Wayland 实证该镜像在
/// show 重置几何后不可信，旧镜像门控会让预置永不触发）；镜像滞后最多造成一次原生层
/// no-op，不存在 toggle 反向还原已最大化窗口的风险。隐藏前未知（采样异常）绝不动作
/// ——行为退回无最大化的基线。
/// </summary>
public static class TrayRecallMaximize
{
    /// <summary>是否需要发出 <c>SetMaximized(true)</c>。<paramref name="maximizedAtHide"/> 取 1=隐藏前最大化 / 0=非最大化 / -1=未知。</summary>
    public static bool ShouldEnsure(int maximizedAtHide) => maximizedAtHide == 1;
}
