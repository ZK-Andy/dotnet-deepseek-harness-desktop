namespace DeepSeek.Harness.Desktop.Services;

/// <summary>从 dsh CLI stdout 解析 <c>dsh web: &lt;url&gt;</c> 行。</summary>
public static class HarnessUrlParser
{
    private const string UrlPrefix = "dsh web: ";

    /// <summary>若 <paramref name="line"/> 含 <c>dsh web:</c> 前缀则返回其绝对 URL，否则 null。</summary>
    /// <param name="line">dsh 进程输出的一行文本。</param>
    /// <returns>解析出的 loopback URL，或 null。</returns>
    public static Uri? TryParse(string? line)
    {
        if (line is null)
        {
            return null;
        }

        var idx = line.IndexOf(UrlPrefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var candidate = line[(idx + UrlPrefix.Length)..].Trim();
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri : null;
    }
}
