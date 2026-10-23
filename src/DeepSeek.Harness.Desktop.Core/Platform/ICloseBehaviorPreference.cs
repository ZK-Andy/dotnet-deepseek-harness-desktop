namespace DeepSeek.Harness.Desktop.Core.Platform;

/// <summary>
/// 「关闭时最小化到托盘」偏好端口（R3 边界抽象；ADR host-boundary-ports）：关窗判据与
/// <c>desktop.closeToTray</c> 命令面只认本端口；文件位置、缺文件/损坏回退默认 true 的容错住实现侧。
/// </summary>
public interface ICloseBehaviorPreference
{
    /// <summary>关闭按钮是否隐藏到托盘（false = 关窗直接退出）。</summary>
    bool HideOnClose { get; }

    /// <summary>写入偏好。落盘失败抛 I/O 异常，由命令面转错误帧。</summary>
    /// <param name="hideOnClose">true = 关闭时隐藏到托盘。</param>
    void Set(bool hideOnClose);
}
