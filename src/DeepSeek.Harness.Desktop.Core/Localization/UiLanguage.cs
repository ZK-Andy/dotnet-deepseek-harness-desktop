namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 语言封闭集（与上游 <c>packages/client/locale</c> 的 <c>LOCALE_IDS</c> + 语言包对齐）：
/// <c>zh</c>/<c>en</c> 是上游的基线语言，ja/es/fr/de/ru/pt 是上游随包分发的语言包。
/// 宿主只认这八种；其余 locale 按上游 <c>FALLBACK_LOCALE = 'en'</c> 回落英文——
/// 上游的理由是「浏览器没指名已注册语言的使用者最不可能读中文」，宿主沿用同一方向。
/// </summary>
public enum UiLanguage
{
    /// <summary>简体中文（上游基线，键集事实源）。</summary>
    Zh,

    /// <summary>English（上游基线；未识别 locale 的回落方向）。</summary>
    En,

    /// <summary>日本語。</summary>
    Ja,

    /// <summary>Español。</summary>
    Es,

    /// <summary>Français。</summary>
    Fr,

    /// <summary>Deutsch。</summary>
    De,

    /// <summary>Русский。</summary>
    Ru,

    /// <summary>Português (Brasil)。</summary>
    Pt,
}

/// <summary>
/// locale 标记 → <see cref="UiLanguage"/> 的单一解析处（正则只此一处）。
/// 判据只看主语言子标签（<c>zh-CN</c>/<c>zh-Hans</c> → <see cref="UiLanguage.Zh"/>），
/// 与上游把 <c>zh</c> 映射到 <c>zh-CN</c>、其余原样使用的行为一致。
/// 分隔符同时认 BCP 47 的 <c>-</c> 与 POSIX 的 <c>_</c>/<c>.</c>：OS 探测面给出的是
/// <c>zh_CN.UTF-8</c>/<c>ja_JP.UTF-8</c> 形态（<c>LC_ALL</c>/<c>LANG</c>），只认 <c>-</c> 会把
/// 这两个标签整串当主标签、连带把中文用户甩到英文回落。
/// </summary>
public static class UiLanguageResolver
{
    /// <summary>主语言子标签的分隔符（BCP 47 <c>-</c>、POSIX <c>_</c> 与 POSIX 修饰符 <c>.</c>）。</summary>
    private static readonly char[] s_primarySeparators = ['-', '_', '.'];

    /// <summary>解析 locale 标记。</summary>
    /// <param name="locale">BCP 47 或 POSIX 风格标记（如 <c>ja-JP</c>、<c>pt_BR.UTF-8</c>）；null/空白按英文回落。</param>
    /// <returns>八种语言之一；未注册语言一律 <see cref="UiLanguage.En"/>。</returns>
    public static UiLanguage Resolve(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return UiLanguage.En;
        }

        string tag = locale.Trim();
        int end = tag.IndexOfAny(s_primarySeparators);
        string primary = end < 0 ? tag : tag[..end];
        return primary.ToLowerInvariant() switch
        {
            "zh" => UiLanguage.Zh,
            "ja" => UiLanguage.Ja,
            "es" => UiLanguage.Es,
            "fr" => UiLanguage.Fr,
            "de" => UiLanguage.De,
            "ru" => UiLanguage.Ru,
            "pt" => UiLanguage.Pt,
            // en 与任何未注册语言同路：上游 FALLBACK_LOCALE 指向英文。
            _ => UiLanguage.En,
        };
    }
}
