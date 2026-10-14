using System.Text;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>
/// 失败面原生对话框正文构建（纯函数可单测；ADR native-failure-surface）：失败原因 + 子进程 stderr 尾部 +
/// 诊断包路径 + 动作说明；尾部行数与单行长度按展示面上限裁剪。文案经 <see cref="UiCopy"/>（UI 文案单一词典）。
/// </summary>
public static class RecoveryDialogText
{
    private const int MaxTailLines = 8;

    private const int MaxLineChars = 160;

    /// <summary>构建失败面正文。</summary>
    /// <param name="reason">人读失败原因（<see cref="UiCopy.RecoveryReason"/> 的域决策产出）。</param>
    /// <param name="stderrTail">子进程 stderr 尾部行（监督器已在重启前留证）。</param>
    /// <param name="exportPath">诊断包落盘路径；空串表示导出失败（正文取失败文案）。</param>
    /// <param name="english">是否取英文分支。</param>
    /// <returns>可直接交原生模态展示的多行正文。</returns>
    public static string Build(string reason, IReadOnlyList<string> stderrTail, string exportPath, bool english)
    {
        var text = new StringBuilder(reason);
        IReadOnlyList<string> tail = stderrTail.TakeLast(MaxTailLines).ToList();
        if (tail.Count > 0)
        {
            text.Append('\n').Append('\n').Append(UiCopy.RecoveryDialogTailHeading(english));
            foreach (string line in tail)
            {
                text.Append('\n').Append(Clip(line));
            }
        }

        text.Append('\n').Append('\n').Append(
            exportPath.Length > 0
                ? UiCopy.RecoveryDialogExportPrefix(english) + exportPath
                : UiCopy.RecoveryDialogExportFailed(english));
        text.Append('\n').Append(UiCopy.RecoveryDialogActions(english));
        return text.ToString();
    }

    private static string Clip(string line) =>
        line.Length <= MaxLineChars ? line : line[..MaxLineChars] + "…";
}
