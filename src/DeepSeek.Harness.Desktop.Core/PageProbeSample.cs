namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// 页面探针原始采样（ADR page-verdict-gate）：探针脚本 → Core 裁决之间的传输形态，
/// 形如 <c>origin@@DSH@@可见文本</c>。只按**首个**分隔符切分（文本内含同串不再解释）。
/// 分隔符是本类型与 <c>PageBridge.WebAuthProbe.Script</c> 之间的协议常量，脚本在编译期拼入之，
/// 两侧不可能漂移；采样残缺（缺分隔符/源为空）时 <see cref="TrySplit"/> 失败 → 裁决 <c>Unknown</c>
/// → 冒烟门禁转红，不静默变绿。
/// </summary>
public static class PageProbeSample
{
    /// <summary>origin 与可见文本之间的分隔符（纯 ASCII，避免控制字符过原生桥的歧义）。</summary>
    public const string Separator = "@@DSH@@";

    /// <summary>
    /// 拆分探针采样。
    /// </summary>
    /// <param name="raw">原始采样；null/空/无分隔符/源为空皆视为不可用。</param>
    /// <param name="origin">输出：页面 origin（分隔符之前）。</param>
    /// <param name="visibleText">输出：可见文本（首个分隔符之后，可空串）。</param>
    /// <returns>可拆分时为 true；否则 false（调用方按未知处理，绝不猜）。</returns>
    public static bool TrySplit(string? raw, out string origin, out string visibleText)
    {
        origin = string.Empty;
        visibleText = string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        int at = raw.IndexOf(Separator, StringComparison.Ordinal);
        if (at <= 0)
        {
            return false;
        }

        origin = raw[..at];
        visibleText = raw[(at + Separator.Length)..];
        return true;
    }
}
