namespace DeepSeek.Harness.Desktop.Core.Platform;

/// <summary>
/// 开机自启端口（R3 边界抽象；ADR host-boundary-ports）：注册表键、XDG desktop entry、
/// LaunchAgents plist 都是平台面，壳侧只经本端口读写——不直调基础设施静态面。
/// </summary>
public interface IAutostart
{
    /// <summary>当前是否已启用开机自启。</summary>
    bool IsEnabled();

    /// <summary>启用/停用开机自启。</summary>
    /// <param name="enabled">true=启用，false=停用。</param>
    /// <returns>生效后的状态（停用成功即 false）。</returns>
    bool SetEnabled(bool enabled);
}
