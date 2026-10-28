using System.Globalization;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 宿主 locale 探测（<see cref="IHostLocaleProbe"/> 的 Infrastructure 实现；
/// ADR core-purity-boundaries）：读环境变量族与 <c>CurrentUICulture</c> 是进程环境面，留在本层。
/// </summary>
public sealed class OsLocaleProbe : IHostLocaleProbe
{
    private readonly Func<string, string?> _env;
    private readonly Func<string> _currentCultureName;

    /// <summary>创建探测器。</summary>
    /// <param name="env">环境变量读取（缺省读进程环境；测试注入假读取，避免改全局环境变量）。</param>
    /// <param name="currentCultureName">当前 UI 文化名读取（缺省读 <see cref="CultureInfo.CurrentUICulture"/>）。</param>
    public OsLocaleProbe(Func<string, string?>? env = null, Func<string>? currentCultureName = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        _currentCultureName = currentCultureName ?? (() => CultureInfo.CurrentUICulture.Name);
    }

    /// <inheritdoc />
    public string? Detect()
    {
        foreach (string key in (string[])["LC_ALL", "LC_MESSAGES", "LANG"])
        {
            string? value = _env(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                // POSIX 形态（zh_CN.UTF-8）归一到 BCP 47（zh-CN）：语言单点与页面 html[lang]
                // 都按 BCP 47 消费，原样传出会让 zh_CN 形态落进「未注册 locale」。
                return ToBcp47(value);
            }
        }

        try
        {
            return _currentCultureName() is { Length: > 0 } name ? name : "zh-CN";
        }
        catch (ArgumentException)
        {
            // CurrentUICulture 含非法中性文化名等边缘形态：回缺省
            return "zh-CN";
        }
    }

    /// <summary>POSIX locale（<c>zh_CN.UTF-8</c>）→ BCP 47（<c>zh-CN</c>）：丢修饰符、<c>_</c> 换 <c>-</c>。</summary>
    /// <param name="value">环境变量原值。</param>
    /// <returns>BCP 47 形态标记；形态不成立时原样返回（由消费方按未注册语言回落）。</returns>
    private static string ToBcp47(string value)
    {
        string trimmed = value.Trim();
        int modifier = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (modifier >= 0)
        {
            trimmed = trimmed[..modifier];
        }

        // C/POSIX 是「未设置」的正式取值，不是语言：交给消费方按未注册语言回落。
        return string.Equals(trimmed, "C", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "POSIX", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed.Replace('_', '-');
    }
}
