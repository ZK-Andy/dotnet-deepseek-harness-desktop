using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 开机自启适配器（<see cref="IAutostart"/> 的 Infrastructure 实现；ADR host-boundary-ports）：
/// 平台面（XDG desktop entry / LaunchAgents plist / 注册表 Run 键）留在本层，壳侧只经端口。
/// </summary>
public sealed class AutostartService : IAutostart
{
    /// <inheritdoc />
    public bool IsEnabled() => Autostart.IsEnabled();

    /// <inheritdoc />
    public bool SetEnabled(bool enabled) => Autostart.SetEnabled(enabled);
}
