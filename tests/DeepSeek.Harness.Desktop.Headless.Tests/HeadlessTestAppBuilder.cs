using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(DeepSeek.Harness.Desktop.Headless.Tests.HeadlessTestAppBuilder))]

namespace DeepSeek.Harness.Desktop.Headless.Tests;

/// <summary>headless 测试应用装配（ADR testing/2026-10-24-ci-desktop-acceptance-via-headless）。
/// Skia 离屏渲染由 <c>UseHeadlessDrawing=false</c> 打开：缺省 headless 画法下
/// <c>CaptureRenderedFrame</c> 返回 null，帧捕获断言不成立。</summary>
public static class HeadlessTestAppBuilder
{
    /// <summary>构造 Avalonia 测试应用（<see cref="AvaloniaTestApplicationAttribute"/> 约定入口）。</summary>
    /// <returns>headless + Skia 离屏的 <see cref="AppBuilder"/>。</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>headless 测试用应用：加载 Fluent 控件主题（与生产宿主 ShellApplication 同源）。
/// 空应用不带主题时 Button/TextBox 无模板——控件没有可命中的视觉子树，鼠标点击静默失效
/// （键盘语义如 IsDefault/IsCancel 不受影响）。</summary>
public sealed class HeadlessTestApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());
}
