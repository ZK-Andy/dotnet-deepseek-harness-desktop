namespace DeepSeek.Harness.Desktop.Core.Platform;

/// <summary>
/// 宿主 WebView 运行时可用性端口（R3 边界抽象；ADR avalonia-g4-webview2-preflight）：
/// Windows 宿主窗口依赖系统 Microsoft Edge WebView2 运行时，缺失时窗口无法创建。
/// 判据落在注册表/文件系统读取（Infrastructure 边界），壳侧只经本端口取结论。
/// </summary>
public interface IWebViewRuntimeProbe
{
    /// <summary>探测宿主窗口所需的 WebView 运行时是否可用。</summary>
    /// <returns>true = 可用（或当前平台不需要该系统运行时）；false = 缺失，须提示用户安装。</returns>
    bool IsAvailable();
}
