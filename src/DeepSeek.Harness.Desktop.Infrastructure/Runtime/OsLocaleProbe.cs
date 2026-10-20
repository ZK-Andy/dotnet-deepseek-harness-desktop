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
                return value;
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
}
