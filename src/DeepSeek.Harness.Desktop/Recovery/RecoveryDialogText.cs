using System.Text;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>
/// 失败面原生对话框正文构建（纯函数可单测；ADR native-failure-surface）：失败原因 + 子进程 stderr 尾部 +
/// 诊断包路径 + 动作说明；尾部行数与单行长度按展示面上限裁剪。文案经 <see cref="UiCopy"/>（UI 文案单一词典）。
/// 原生模态无滚动且宽度不可控（插件只传标题与正文），故尾部节另做两件事：重复行折叠（上游长警告常整段重复，
/// 2026-10-15 实机 8 行 peer 告警 ≈ 1300 字符）与尾部节预算装箱（超长会把按钮顶出屏幕；原因/导出/动作恒在）。
/// </summary>
public static class RecoveryDialogText
{
    private const int MaxTailLines = 8;

    private const int MaxLineChars = 160;

    /// <summary>尾部节预算（字符）：原因 + 导出 + 动作说明恒在，尾部节按此预算从最新往最旧装。</summary>
    private const int MaxTotalChars = 600;

    /// <summary>构建失败面正文。</summary>
    /// <param name="reason">人读失败原因（<see cref="UiCopy.RecoveryReason"/> 的域决策产出）。</param>
    /// <param name="stderrTail">子进程 stderr 尾部行（监督器已在重启前留证）。</param>
    /// <param name="exportPath">诊断包落盘路径；空串表示导出失败（正文取失败文案）；null 表示本次不涉及
    /// 诊断包导出（如启动失败），整节省略——不能拿「导出失败」冒充「未尝试」。</param>
    /// <param name="english">是否取英文分支。</param>
    /// <param name="actions">动作说明行（缺省取崩溃恢复口径）；启动失败须传
    /// <see cref="UiCopy.StartupFailureActions"/>——「继续在后台自动恢复」对启动失败是假话。</param>
    /// <returns>可直接交原生模态展示的正文（尾部节按 600 字符预算装箱；原因/动作恒在，导出节随入参）。</returns>
    public static string Build(
        string reason,
        IReadOnlyList<string> stderrTail,
        string? exportPath,
        bool english,
        string? actions = null)
    {
        string? export = exportPath is null
            ? null
            : exportPath.Length > 0
                ? UiCopy.RecoveryDialogExportPrefix(english) + exportPath
                : UiCopy.RecoveryDialogExportFailed(english);
        string actionLine = actions ?? UiCopy.RecoveryDialogActions(english);
        List<string> lines = FitTail(
            CollapseRepeats(stderrTail.TakeLast(MaxTailLines).Select(Clip).ToList()),
            reason.Length + (export?.Length ?? 0) + actionLine.Length,
            english);

        var text = new StringBuilder(reason);
        if (lines.Count > 0)
        {
            text.Append('\n').Append('\n').Append(UiCopy.RecoveryDialogTailHeading(english));
            foreach (string line in lines)
            {
                text.Append('\n').Append(line);
            }
        }

        if (export is not null)
        {
            text.Append('\n').Append('\n').Append(export);
        }
        text.Append('\n').Append(actionLine);
        return text.ToString();
    }

    /// <summary>重复行折叠（纯函数）：保首次出现序，重复行记 <c>(×N)</c> 后缀（语言中立，不进词典）。</summary>
    /// <param name="lines">已裁剪的尾部行。</param>
    /// <returns>折叠后的行（单次出现行保持原样）。</returns>
    private static List<string> CollapseRepeats(List<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (string line in lines)
        {
            if (counts.TryGetValue(line, out int n))
            {
                counts[line] = n + 1;
            }
            else
            {
                counts[line] = 1;
                order.Add(line);
            }
        }

        return order.Select(l => counts[l] > 1 ? l + $" (×{counts[l]})" : l).ToList();
    }

    /// <summary>尾部行装箱：去重后从最新往最旧装，固定部 + 标题 + 已装行恒 ≤ 尾部节预算；一行都装不下即整个尾部节退场。</summary>
    /// <param name="lines">折叠后的尾部行（原序）。</param>
    /// <param name="fixedLength">原因 + 导出 + 动作说明三段合计长度。</param>
    /// <param name="english">是否取英文分支（标题长度计入预算）。</param>
    /// <returns>装下的尾部行（原序子集）。</returns>
    private static List<string> FitTail(List<string> lines, int fixedLength, bool english)
    {
        var kept = new List<string>();
        if (lines.Count == 0)
        {
            return kept;
        }

        int used = fixedLength + UiCopy.RecoveryDialogTailHeading(english).Length + 5; // 两个 \n\n + 一个 \n
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            int cost = lines[i].Length + 1; // 行前换行
            if (used + cost > MaxTotalChars)
            {
                break;
            }

            used += cost;
            kept.Insert(0, lines[i]);
        }

        return kept;
    }

    private static string Clip(string line) =>
        line.Length <= MaxLineChars ? line : line[..MaxLineChars] + "…";
}
