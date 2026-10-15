namespace DeepSeek.Harness.Desktop.Core.Recovery;

/// <summary>
/// 遗留原生对话框收割端口（R3：外部边界经接口；ADR failure-surface-dialog-defects）：失败面原生模态是
/// 插件起的子进程（Linux zenity/kdialog），展示等待不跟应用退出令牌——应用退出时它会成孤儿留在屏幕上。
/// 退出编排在收口末尾调一次本端口做 best-effort 收割（只杀本进程的直接子进程且只认对话框工具名，零误杀方向）。
/// </summary>
public interface IDialogOrphanReaper
{
    /// <summary>收割本进程遗留的原生对话框子进程（best-effort：读不到进程表/杀不掉只留痕，不抛）。</summary>
    void Reap();
}
