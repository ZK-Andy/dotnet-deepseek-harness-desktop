using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 日志内容脱敏（纯函数）：挂在 <see cref="HostLog.Write"/> 唯一出口，stdout 与
/// <c>host.log</c> 双写同时覆盖——诊断 zip 白名单收录 host.log 原文且是用户主动外发
/// 的唯一通道（贴 issue / 发社区），脱敏必须发生在落盘之前。
/// 分层：Cookie/Authorization 头整值、URL 敏感查询键、引号键值对（JSON/YAML 形态）、
/// 裸 token 形状兜底（<c>sk-</c> 前缀与 32+ 位十六进制）。误伤方向刻意偏安全：
/// 宁可多遮影响可读性，不可漏遮造成泄漏；来源含上游 dsh 子进程的不可控输出。
/// </summary>
internal static partial class SecretMasker
{
    // 头部整值：Cookie/Set-Cookie/Authorization 后的整行值没有保留价值
    [GeneratedRegex("(?i)\\b(Cookie|Set-Cookie|Authorization)\\b\\s*:\\s*[^\\r\\n]*")]
    private static partial Regex HeaderLine();

    // URL 查询键：[?&]key=value 只遮 value，保留键名与 URL 结构供排障
    [GeneratedRegex("(?i)([?&])(token|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|secret|client[_-]?secret|password|passwd|session|sig|signature)=([^&\\s'\"<>]+)")]
    private static partial Regex UrlSecretKey();

    // 引号键值对："apiKey": "…" / 'password': '…'（JSON/YAML 形态，键名引号可选），
    // 键名限定敏感集合防误伤
    [GeneratedRegex("(?i)([\"']?(?:api[_-]?key|token|access[_-]?token|refresh[_-]?token|secret|client[_-]?secret|password|passwd|authorization)[\"']?\\s*[:=]\\s*)(['\"])[^'\"]*\\2")]
    private static partial Regex QuotedAssignment();

    // 裸形状兜底：sk- 前缀（DEEPSEEK_API_KEY 形态）保留前缀便于识别被遮对象
    [GeneratedRegex("\\bsk-[A-Za-z0-9][A-Za-z0-9_-]{7,}")]
    private static partial Regex SkToken();

    // 裸形状兜底：32–64 位连续十六进制（hex 摘要/会话 id 形态）；限字符集避免误伤普通词与路径
    [GeneratedRegex("\\b[A-Fa-f0-9]{32,64}\\b")]
    private static partial Regex HexToken();

    /// <summary>返回脱敏后的日志文本；null 原样透传。</summary>
    public static string? Mask(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        // 层序刻意安排：结构化遮罩先行（保留键名与上下文），头行整值兜底殿后——
        // 它会吞掉行内自冒号起的全部剩余内容，必须最后执行
        var masked = UrlSecretKey().Replace(message, "$1$2=***");
        masked = QuotedAssignment().Replace(masked, "$1$2***$2");
        masked = SkToken().Replace(masked, "sk-***");
        masked = HexToken().Replace(masked, "***");
        masked = HeaderLine().Replace(masked, static m =>
        {
            var colon = m.Value.IndexOf(':');
            return m.Value[..(colon + 1)] + " ***";
        });
        return masked;
    }
}
