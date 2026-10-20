using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>
/// 失败面承载适配器（R3：原生对话框与页内恢复面都是 Presentation 面；ADR native-failure-surface 的
/// <c>StartupSequence</c> 各段整体搬迁，零行为变更）：原生通道可用即弹模态（正文 = 失败原因 + stderr 尾部 +
/// 诊断包路径，退出动作挂显式 true），不可用或承载故障时由用例回落本类的页内恢复面。
/// </summary>
internal sealed class RecoverySurface : IRecoverySurface
{
    private const int PageTailLines = 12;

    private readonly HarnessRuntimeHost _host;
    private readonly CurrentWindowAccessor _accessor;
    private readonly IFailureDialog _dialog;
    private readonly IDiagnosticsExport _diagnosticsExport;
    private readonly UiLocale _uiLocale;
    private readonly Func<IRynWindow> _window;
    private readonly TrayController _tray;
    private readonly Action<string> _log;

    /// <summary>创建适配器（窗口以提供者惰性供给：装配期窗口尚不存在，调用点必在窗口就绪之后）。</summary>
    /// <param name="host">运行时宿主（stderr 尾部来源）。</param>
    /// <param name="accessor">当前窗口访问器（页内恢复面的注入靶点）。</param>
    /// <param name="dialog">原生失败模态端口。</param>
    /// <param name="diagnosticsExport">诊断包导出端口（正文里的求助路径）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点（文案分支）。</param>
    /// <param name="window">Ryn 窗口提供者（退出动作的关窗靶点）。</param>
    /// <param name="tray">托盘控制器（关窗闸门的批准入口）。</param>
    /// <param name="log">日志出口。</param>
    public RecoverySurface(
        HarnessRuntimeHost host,
        CurrentWindowAccessor accessor,
        IFailureDialog dialog,
        IDiagnosticsExport diagnosticsExport,
        UiLocale uiLocale,
        Func<IRynWindow> window,
        TrayController tray,
        Action<string> log)
    {
        _host = host;
        _accessor = accessor;
        _dialog = dialog;
        _diagnosticsExport = diagnosticsExport;
        _uiLocale = uiLocale;
        _window = window;
        _tray = tray;
        _log = log;
    }

    /// <inheritdoc />
    public bool HasNativeChannel => _dialog.IsAvailable;

    /// <inheritdoc />
    public async Task ShowNativeAsync(string reason, CancellationToken ct)
    {
        // 诊断包导出走端口（ADR host-boundary-ports）：失败面只取路径——导出失败时结果里路径为空串，
        // 原因已由实现记日志（原生对话框正文只展示可读路径，不塞异常栈）。
        DiagnosticsExportOutcome diagnostics = _diagnosticsExport.Export();
        string message = RecoveryDialogText.Build(
            reason, _host.StderrTail, diagnostics.Ok ? diagnostics.Path : string.Empty, _uiLocale.IsEnglish);
        bool quit = await _dialog
            .ConfirmAsync(UiCopy.RecoveryDialogTitle(_uiLocale.IsEnglish), message, ct)
            .ConfigureAwait(false);
        if (quit)
        {
            ExitFromFailureSurface();
        }
    }

    /// <inheritdoc />
    public ValueTask ShowPageAsync(string reason)
    {
        IReadOnlyList<string> tail = _host.StderrTail;
        _ = _accessor.Current.EvaluateJavaScriptAsync(
            RecoveryPageBuilder.BuildScript(reason, tail.TakeLast(PageTailLines).ToList(), _uiLocale.IsEnglish));
        return ValueTask.CompletedTask;
    }

    /// <summary>失败面「退出应用」动作：与托盘/恢复页退出同一条顺序契约——先批准关窗闸门再 Close
    /// （hide-to-tray 拦截下未批准的 Close 会吞成隐藏）。</summary>
    private void ExitFromFailureSurface()
    {
        _log("[host] 用户在失败面选择退出应用");
        try
        {
            _tray.CloseGate.ApproveExit();
            _window().Close();
        }
        catch (Exception ex)
        {
            _log($"[host] 失败面退出动作失败：{ex.Message}");
        }
    }
}
