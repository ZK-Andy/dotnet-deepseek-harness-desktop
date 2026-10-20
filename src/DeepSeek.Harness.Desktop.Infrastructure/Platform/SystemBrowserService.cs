using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 系统浏览器适配器（<see cref="ISystemBrowser"/> 的 Infrastructure 实现；ADR host-boundary-ports）：
/// xdg-open / shell execute 等进程外部交互留在本层，壳侧只经端口。
/// </summary>
public sealed class SystemBrowserService : ISystemBrowser
{
    /// <inheritdoc />
    public bool Open(string url) => SystemBrowser.Open(url);
}
