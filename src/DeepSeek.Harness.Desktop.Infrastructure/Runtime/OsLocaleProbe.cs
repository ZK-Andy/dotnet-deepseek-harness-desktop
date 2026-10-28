using System.Globalization;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 宿主 locale 探测（<see cref="IHostLocaleProbe"/> 的 Infrastructure 实现；
/// ADR core-purity-boundaries）：读环境变量族与 <c>CurrentUICulture</c> 是进程环境面，留在本层。
/// 三级回退的末级取 <see cref="UiLanguageResolver.NoSourceLocale"/>——生产启动路径真正走到的
/// 「无来源」缺省在此，不在 <c>UiLocale.ResolveInitial</c> 的守卫分支（ADR ui-locale-no-source-single-source）。
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
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // POSIX 形态（zh_CN.UTF-8）归一到 BCP 47（zh-CN）：语言单点与页面 html[lang]
            // 都按 BCP 47 消费，原样传出会让 zh_CN 形态落进「未注册 locale」。
            // 归一后为空（整串只有一个 BOM）等于未设置：顺延下一个键，不把它当 locale 传出去。
            string tag = ToBcp47(value);
            if (!string.IsNullOrWhiteSpace(tag))
            {
                return tag;
            }
        }

        try
        {
            return _currentCultureName() is { Length: > 0 } name ? name : UiLanguageResolver.NoSourceLocale;
        }
        catch (ArgumentException)
        {
            // CurrentUICulture 含非法中性文化名等边缘形态：回缺省
            return UiLanguageResolver.NoSourceLocale;
        }
    }

    /// <summary>POSIX locale（<c>zh_CN.UTF-8</c>）→ BCP 47（<c>zh-CN</c>）：丢修饰符、<c>_</c> 换 <c>-</c>。</summary>
    /// <param name="value">环境变量原值。</param>
    /// <returns>BCP 47 形态标记；形态不成立时原样返回（由消费方按未注册语言回落）。</returns>
    private static string ToBcp47(string value)
    {
        // BOM 不算空白（.NET 的 Trim 不删它）：环境变量被 Windows 侧工具写过就会带 BOM，
        // 留着会让主语言子标签变成 "\uFEFFzh"、整个语言族落进「未注册」→ 英文回落。
        string trimmed = value.Replace("\uFEFF", string.Empty, StringComparison.Ordinal).Trim();
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
