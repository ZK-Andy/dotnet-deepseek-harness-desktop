namespace DeepSeek.Harness.Desktop.Tray;

/// <summary>托盘菜单与 UI 语言的绑定（ADR tray-menu-locale-rebuild）：订阅 <see cref="UiLocale.Changed"/>，
/// 语言实际变化时调用重建回调；构建与挂载由回调负责（生产侧在 UI 线程完成，见 <c>ShellTray</c>）。
/// 与托盘同生灭：<see cref="Dispose"/> 退订，退订后的事件不再触发回调。本类不碰托盘框架对象，
/// 也不假设回调在哪个线程执行。</summary>
internal sealed class TrayMenuRebuild : IDisposable
{
    private readonly UiLocale _locale;
    private readonly Action _rebuild;
    private int _disposed;

    /// <summary>创建绑定；首份菜单由调用方随托盘图标一起构造（本类只负责变化后的重建）。</summary>
    /// <param name="locale">UI 语言单点（事件源）。</param>
    /// <param name="rebuild">重建回调（按当前语言重造菜单并挂载；线程与异常边界由回调负责）。</param>
    internal TrayMenuRebuild(UiLocale locale, Action rebuild)
    {
        _locale = locale;
        _rebuild = rebuild;
        _locale.Changed += OnLocaleChanged;
    }

    /// <summary>语言变化回调：调用一次重建；已释放即丢弃。</summary>
    private void OnLocaleChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _rebuild();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _locale.Changed -= OnLocaleChanged;
    }
}
