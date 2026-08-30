using System.Globalization;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主 UI 语言单点（ADR host-ui-locale）：托盘菜单与横幅文案的 locale 依据。
/// 语言值经 dsh→companion→宿主单向桥接（companion 监听 <c>html[lang]</c> 上报），
/// OS locale 仅作首报前的缺省；非 <c>en*</c> 一律按中文——对齐 dsh 字典查找链的兜底方向。
/// </summary>
public sealed class UiLocale
{
    private volatile string _locale = DetectOsLocale();

    /// <summary>当前 locale（如 <c>zh-CN</c>/<c>en</c>，原样保存便于诊断展示）。</summary>
    public string Current => _locale;

    /// <summary>UI 文案是否取英文分支。</summary>
    public bool IsEnglish => _locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>确认按钮文案（横幅共用，随当前 locale）。</summary>
    public string OkLabel => IsEnglish ? "OK" : "知道了";

    /// <summary>locale 发生实际变化时触发（托盘菜单重建等消费方订阅）。</summary>
    public event Action? Changed;

    /// <summary>上报 locale：trim 归一化后保存（大小写敏感判等）；值未变化不触发事件。</summary>
    public void Set(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        string previous = _locale;
        _locale = locale.Trim();
        if (!string.Equals(previous, _locale, StringComparison.Ordinal))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>OS locale 兜底探测：Unix 读环境变量族，Windows 读 CurrentUICulture；失败回中文（本产品主要受众）。</summary>
    private static string DetectOsLocale()
    {
        foreach (string key in (string[])["LC_ALL", "LC_MESSAGES", "LANG"])
        {
            string? value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        try
        {
            return CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : "zh-CN";
        }
        catch (ArgumentException)
        {
            // CurrentUICulture 含非法中性文化名等边缘形态：回缺省
            return "zh-CN";
        }
    }
}
