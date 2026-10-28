namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 语言单点（ADR host-ui-locale）：托盘菜单、横幅与引导页文案的 locale 依据。
/// 语言值经 dsh→companion→宿主单向桥接（companion 监听 <c>html[lang]</c> 上报）并持久化，
/// 初始值取上次上报（记不住时回退宿主 locale 探测端口，端口未接线即回中文）。
/// 具体语言分支由 <see cref="UiLanguageResolver"/> 单点判定：八种已注册语言取各自字典，
/// 其余一律回落英文——与上游 <c>FALLBACK_LOCALE = 'en'</c> 同向（ADR ui-copy-eight-locales）。
/// </summary>
public sealed class UiLocale
{
    private readonly IUiLocaleStore? _store;
    private readonly IHostLocaleProbe? _probe;
    private volatile string _locale;

    /// <summary>创建语言单点。</summary>
    /// <param name="store">上次 locale 的持久化端口（可选；缺省则每次启动都从探测端口的缺省起）。</param>
    /// <param name="probe">宿主 locale 探测端口（可选；缺省则回中文——读环境变量在 Infrastructure 实现）。</param>
    public UiLocale(IUiLocaleStore? store = null, IHostLocaleProbe? probe = null)
    {
        _store = store;
        _probe = probe;
        _locale = ResolveInitial(store, probe);
    }

    /// <summary>当前 locale（如 <c>zh-CN</c>/<c>ja-JP</c>，原样保存便于诊断展示）。</summary>
    public string Current => _locale;

    /// <summary>当前 UI 语言（八种封闭集之一；未注册 locale 回落到英文）。</summary>
    public UiLanguage Language => UiLanguageResolver.Resolve(_locale);
    /// <summary>
    /// 未接线语言单点时的缺省语言（各消费方的「可选 UiLocale」参数共用此一处）：
    /// 中文——本产品主要受众，且与 <see cref="ResolveInitial"/> 探测不出时回中文同向。
    /// 与 <see cref="UiLanguageResolver.Resolve"/> 的「未注册 locale 回落英文」不矛盾：
    /// 那是<b>已知 locale 但语言未注册</b>的情形，这里是<b>没有 locale 来源</b>的缺省展示。
    /// </summary>
    public static UiLanguage DefaultLanguage => UiLanguage.Zh;

    /// <summary>确认按钮文案（横幅共用，随当前 locale；字面量单一事实源在 <see cref="UiCopy"/>）。</summary>
    public string OkLabel => UiCopy.OkLabel(Language);

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
    /// <see cref="Language"/>（<see cref="UiLanguageResolver"/>）判。持久化回读与 companion 上报共用
    /// 此判据——规则单一事实源。注意它<b>不接受</b> POSIX 形态（<c>zh_CN</c>）：归一发生在探测面
    /// （<c>OsLocaleProbe</c>），此处保持 BCP 47 契约。</summary>
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
    private static string ResolveInitial(IUiLocaleStore? store, IHostLocaleProbe? probe)
    {
        string? persisted = store?.Load();
        if (persisted is not null && IsPlausibleLocale(persisted))
        {
            return persisted;
        }

        // 探测面在 Infrastructure（Core 不读环境变量）；探测不出或未接线即回中文（本产品主要受众）。
        string? detected = probe?.Detect();
        return string.IsNullOrWhiteSpace(detected) ? "zh-CN" : detected;
    }
}
