using System.Globalization;

namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 语言单点（ADR host-ui-locale）：托盘菜单、横幅与引导页文案的 locale 依据。
/// 语言值经 dsh→companion→宿主单向桥接（companion 监听 <c>html[lang]</c> 上报）并持久化，
/// 初始值取上次上报（记不住时回退 OS locale）；非 <c>en*</c> 一律按中文——对齐 dsh
/// 字典查找链的兜底方向。
/// </summary>
public sealed class UiLocale
{
    private readonly IUiLocaleStore? _store;
    private volatile string _locale;

    /// <summary>创建语言单点。</summary>
    /// <param name="store">上次 locale 的持久化端口（可选；缺省则每次启动都从 OS locale 起）。</param>
    public UiLocale(IUiLocaleStore? store = null)
    {
        _store = store;
        _locale = ResolveInitial(store);
    }

    /// <summary>当前 locale（如 <c>zh-CN</c>/<c>en</c>，原样保存便于诊断展示）。</summary>
    public string Current => _locale;

    /// <summary>UI 文案是否取英文分支。</summary>
    public bool IsEnglish => _locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>确认按钮文案（横幅共用，随当前 locale；字面量单一事实源在 <see cref="UiCopy"/>）。</summary>
    public string OkLabel => UiCopy.OkLabel(IsEnglish);

    /// <summary>locale 发生实际变化时触发（托盘菜单重建等消费方订阅）。</summary>
    public event Action? Changed;

    /// <summary>上报 locale：trim 归一化后保存（大小写敏感判等，供下次启动复用）；值未变化不触发事件。</summary>
    public void Set(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        string previous = _locale;
        _locale = locale.Trim();
        if (string.Equals(previous, _locale, StringComparison.Ordinal))
        {
            return;
        }

        _store?.Save(_locale);
        Changed?.Invoke();
    }

    /// <summary>宽松合法性：字母数字段（<c>-</c> 分隔）形态即可（<c>zh-CN</c>/<c>en</c>）；具体语言分支由
    /// <see cref="IsEnglish"/> 判。持久化回读与 companion 上报共用此判据——规则单一事实源。</summary>
    /// <param name="locale">待判定的语言标记。</param>
    /// <returns>形态合法为 true。</returns>
    public static bool IsPlausibleLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > 35)
        {
            return false;
        }

        foreach (string part in locale.Split('-'))
        {
            if (part.Length is < 2 or > 8 || !part.All(char.IsAsciiLetterOrDigit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>初始值：上次上报的 locale（形态合法才采信）→ OS locale 兜底。</summary>
    private static string ResolveInitial(IUiLocaleStore? store)
    {
        string? persisted = store?.Load();
        return persisted is not null && IsPlausibleLocale(persisted) ? persisted : DetectOsLocale();
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
