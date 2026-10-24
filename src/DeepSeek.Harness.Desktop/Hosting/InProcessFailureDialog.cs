using Avalonia.Controls;
using Avalonia.Threading;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// <see cref="IFailureDialog"/> 的进程内实现（ADR 2026-10-23-avalonia-failure-dialog）：
/// 可用性恒真（不探测外部工具），模态由 <see cref="FailureDialogWindow"/> 在 UI 线程建。
/// 契约与 <see cref="Core.Recovery.IFailureDialog"/> 同一条：<b>只有显式 true 才触发破坏性动作</b>——属主缺失、窗口建不出来、
/// 用户直接关闭一律回 false，调用方据此继续留在窗口。
/// </summary>
internal sealed class InProcessFailureDialog : IFailureDialog
{
    private readonly Func<Window?> _owner;
    private readonly UiLocale _locale;
    private readonly Action<string>? _log;

    /// <summary>创建适配器。</summary>
    /// <param name="owner">属主窗口取法（调用期满，窗口可能已关闭）。</param>
    /// <param name="locale">UI 语言单点（按钮文案）。</param>
    /// <param name="log">日志出口（可选）。</param>
    public InProcessFailureDialog(Func<Window?> owner, UiLocale locale, Action<string>? log = null)
    {
        _owner = owner;
        _locale = locale;
        _log = log;
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = ct.Register(() => completion.TrySetResult(false));
        Dispatcher.UIThread.Post(() => _ = ShowCoreAsync(completion, title, message));
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>UI 线程内的模态承载：任何失败按通道故障回 false（契约：false 不许可破坏性动作）。</summary>
    /// <param name="completion">结果栅栏。</param>
    /// <param name="title">窗口标题。</param>
    /// <param name="message">正文。</param>
    /// <returns>完成的任务。</returns>
    private async Task ShowCoreAsync(TaskCompletionSource<bool> completion, string title, string message)
    {
        try
        {
            Window? owner = _owner();
            if (owner is null)
            {
                _log?.Invoke("[host] 启动失败对话框无属主窗口，按通道故障处理");
                completion.TrySetResult(false);
                return;
            }

            if (!owner.IsVisible)
            {
                // 属主在托盘里时 ShowDialog 会抛（Avalonia 要求父窗可见）：先把主窗叫回来再弹，
                // 否则模态根本不出现、进程留在托盘。
                _log?.Invoke("[host] 启动失败对话框的属主窗口不可见：先唤回主窗");
                owner.Show();
                owner.Activate();
            }

            bool quit = await FailureDialogWindow.ShowAsync(
                owner,
                title,
                message,
                UiCopy.RecoveryExitButton(_locale.IsEnglish),
                UiCopy.DialogStayLabel(_locale.IsEnglish)).ConfigureAwait(true);
            completion.TrySetResult(quit);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[host] 启动失败对话框承载失败：{ex.Message}");
            completion.TrySetResult(false);
        }
    }
}
