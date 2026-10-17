namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// WebKit 渲染路径的引擎环境变量名（常量单点，与 <see cref="WebkitSandboxPolicy.DisableEnvVar"/> 同形）：
/// 值由上游 Ryn 按 <c>RynOptions.LinuxRenderingMode</c> 写入，宿主只读不写——读点唯一在桌面壳的启动摘要
/// （ADR webview-shared-memory-rendering）。
/// </summary>
public static class WebkitRenderingEnv
{
    /// <summary>强制共享内存呈现开关：Ryn 在渲染模式为 <c>SharedMemory</c> 时写 <c>1</c>，且只 set 不 unset
    /// （故应用内重启会继承上一轮值，回退需冷启动）。</summary>
    public const string ForceSharedMemory = "WEBKIT_DMABUF_RENDERER_FORCE_SHM";
}
