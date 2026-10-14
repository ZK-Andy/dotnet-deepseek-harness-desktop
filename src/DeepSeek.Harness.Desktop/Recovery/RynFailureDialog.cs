using Ryn.Plugins.Dialog;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>
/// 失败面原生对话框适配器（Ryn.Plugins.Dialog；ADR native-failure-surface）：Windows <c>MessageBoxW</c> / macOS
/// <c>osascript</c> / Linux <c>zenity</c>|<c>kdialog</c>；可用性先判。同步模态封送池线程；<b>通道故障一律回 false</b>。</summary>
internal sealed class RynFailureDialog : IFailureDialog
{
    private readonly Func<string, bool> _isExecutable;
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<string>? _log;

    /// <summary>创建适配器。</summary>
    /// <param name="log">日志输出（可选）。</param>
    /// <param name="isExecutable">PATH 可执行性判定（生产查存在 + Unix 执行位，与插件 <c>which</c> 同口径）。</param>
    /// <param name="confirm">原生确认通道（生产用 <c>DialogCommands.Confirm</c>，故障恒回 false）。</param>
    internal RynFailureDialog(
        Action<string>? log = null,
        Func<string, bool>? isExecutable = null,
        Func<string, string, bool>? confirm = null)
    {
        _log = log;
        _isExecutable = isExecutable ?? IsExecutable; // verify-code-conventions: ignore 平台探测：适配器即原生通道的边界面（与本类封装的 dialog 通道同界）
        _confirm = confirm ?? DialogCommands.Confirm;
    }

    /// <summary>文件存在且带执行位（<c>which</c> 口径）；悬空链/竞态删除等探测异常留痕后按不可执行。</summary>
    private bool IsExecutable(string path)
    {
        try
        {
            return File.Exists(path) // verify-code-conventions: ignore 平台探测（适配器即原生通道边界）
                && (OperatingSystem.IsWindows()
                    || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0); // verify-code-conventions: ignore 平台探测（适配器即原生通道边界）
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[host] 对话框工具可执行性探测失败（按不可执行处理）：{path}：{ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => DialogToolAvailability.IsAvailable(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        OperatingSystem.IsLinux(),
        Environment.GetEnvironmentVariable("PATH"),
        _isExecutable);

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        _log?.Invoke("[host] 失败面原生对话框已弹出，等待用户处置");
        // 同步模态隔离到池线程，取消只放弃等待；Task.Run 不收 ct（模态随用户处置或进程退出消失）。
        return await Task.Run(() => _confirm(title, message), CancellationToken.None)
            .WaitAsync(ct)
            .ConfigureAwait(false);
    }
}
