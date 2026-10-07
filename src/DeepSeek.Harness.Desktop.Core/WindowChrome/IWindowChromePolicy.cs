namespace DeepSeek.Harness.Desktop.Core.WindowChrome;

/// <summary>
/// 窗口 chrome 几何端口（R3：接口在 Core；默认实现同为纯逻辑，随端口同住 Core——
/// 纯策略不住适配器。组合根机制收官（ADR <c>proposed/architecture/2026-10-08-composition-mechanism-container-assembly</c>）
/// 后由容器注入，在此之前调用方经 <see cref="WindowChromePolicy.Default"/> 取用）。
/// </summary>
public interface IWindowChromePolicy
{
    /// <summary>由顶带高度解出三变量（见 <see cref="ChromeInsets.Resolve"/>）。</summary>
    /// <param name="heightPx">顶带高度（CSS 像素）。</param>
    /// <param name="fullscreen">是否全屏态（本壳当前恒 false，见该参数文档）。</param>
    /// <returns>解出的三变量几何。</returns>
    ChromeInsets ResolveInsets(int heightPx, bool fullscreen = false);
}

/// <summary>默认实现：直调 <see cref="ChromeInsets.Resolve"/>（纯函数，无外部依赖）。</summary>
public sealed class WindowChromePolicy : IWindowChromePolicy
{
    /// <summary>共享默认实例（<c>CaptionBarOptions.Default</c> 同型：在容器注入就位前，调用方免 new 取用，
    /// 组合根 new 配额零消耗）。</summary>
    public static IWindowChromePolicy Default { get; } = new WindowChromePolicy();

    /// <inheritdoc />
    public ChromeInsets ResolveInsets(int heightPx, bool fullscreen = false) =>
        ChromeInsets.Resolve(heightPx, fullscreen);
}
