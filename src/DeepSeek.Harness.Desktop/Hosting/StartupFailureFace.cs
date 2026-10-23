
namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 启动失败的处置用例（ADR 2026-10-23-avalonia-failure-dialog）：dsh 运行时起不来时，把
/// 「原因 + stderr 尾部 + 动作说明」交给失败面模态，只有用户显式选择退出才走关窗。
/// 与崩溃恢复面<b>刻意分开</b>：崩溃恢复面说的是「正在自动重启」（<c>RecoveryDialogActions</c>），
/// 而启动失败没有监督循环——用同一句文案会把「没人重试」说成「正在重试」。
/// 本类不碰 Avalonia 类型（模态经 <see cref="IFailureDialog"/> 端口），故可 headless 单测。
/// </summary>
internal sealed class StartupFailureFace
{
    private readonly IFailureDialog _dialog;
    private readonly Func<IReadOnlyList<string>> _stderrTail;
    private readonly UiLocale _uiLocale;
    private readonly Action _quit;
    private readonly Action<string> _log;

    /// <summary>创建处置用例。</summary>
    /// <param name="dialog">失败面模态端口。</param>
    /// <param name="stderrTail">子进程 stderr 尾部取法（启动失败时可能为空）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点。</param>
    /// <param name="quit">用户选择退出时的动作（批准关窗闸门 + 关窗）。</param>
    /// <param name="log">日志出口。</param>
    public StartupFailureFace(
        IFailureDialog dialog,
        Func<IReadOnlyList<string>> stderrTail,
        UiLocale uiLocale,
        Action quit,
        Action<string> log)
    {
        _dialog = dialog;
        _stderrTail = stderrTail;
        _uiLocale = uiLocale;
        _quit = quit;
        _log = log;
    }

    /// <summary>展示启动失败面并按其结果处置；模态故障只留痕，绝不当作退出许可。</summary>
    /// <param name="ct">取消令牌（只影响等待，不打断已弹出的模态）。</param>
    /// <returns>完成的任务。</returns>
    public async Task ShowAsync(CancellationToken ct)
    {
        bool quit;
        try
        {
            // 正文构建也在 try 内：尾部取法/文案构建的任何抛出都必须落日志，不能静默消失。
            bool english = _uiLocale.IsEnglish;
            string message = RecoveryDialogText.Build(
                UiCopy.StartupFailureReason(english),
                _stderrTail(),
                exportPath: null,
                english,
                actions: UiCopy.StartupFailureActions(english));
            quit = await _dialog
                .ConfirmAsync(UiCopy.StartupFailureTitle(english), message, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退出路径：模态随进程收尾消失，无需再处置。
            return;
        }
        catch (Exception ex)
        {
            // 契约：通道故障回 false 的语义在这里由异常路径补齐——失败面消失不能变成退出许可。
            _log($"[host] 启动失败面承载失败：{ex.Message}");
            return;
        }

        if (!quit)
        {
            _log("[host] 用户在启动失败对话框选择留在窗口");
            return;
        }

        _log("[host] 用户在启动失败对话框选择退出应用");
        _quit();
    }
}
