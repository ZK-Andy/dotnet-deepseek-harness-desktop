using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>Avalonia 应用对象：注册 Fluent 主题，主窗创建后触发运行时起步（G0 片，ADR
/// proposed/architecture/2026-10-22-avalonia-host-migration）。关闭主窗即结束 lifetime。</summary>
internal sealed class ShellApplication : Application
{
    private readonly ShellStartup _startup;
    private readonly bool _devTools;

    /// <summary>创建应用对象。</summary>
    /// <param name="startup">运行时起步（dsh spawn 与铸币）。</param>
    /// <param name="devTools">是否开启 WebView 调试器。</param>
    public ShellApplication(ShellStartup startup, bool devTools)
    {
        _startup = startup;
        _devTools = devTools;
    }

    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new ShellWindow(_startup.InitialUri, _devTools);
            desktop.MainWindow = window;
            window.Opened += (_, _) => _startup.Begin();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
