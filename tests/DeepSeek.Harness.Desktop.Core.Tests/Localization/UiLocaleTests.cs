
namespace DeepSeek.Harness.Desktop.Core.Tests.Localization;

/// <summary>宿主 UI 语言单点契约（ADR host-ui-locale + ui-copy-bilingual-completion）：
/// 归一化、变化事件、英文分支判定、OS 缺省、上次上报值的持久化与回读判据。</summary>
public class UiLocaleTests
{
    /// <summary>验证 Set 更新 Current 且仅在值实际变化时触发一次 Changed，同值设置不重复触发。</summary>
    [Fact]
    public void Set_NewValue_UpdatesAndFiresChanged()
    {
        var ui = new UiLocale();
        int fired = 0;
        ui.Changed += () => fired++;

        ui.Set("en");
        ui.Set("en"); // 同值不重复触发
        ui.Set("zh-CN");

        Assert.Equal("zh-CN", ui.Current);
        Assert.Equal(2, fired);
    }

    /// <summary>八语言封闭集：已注册语言各归其位，未注册语言回落英文
    /// （ADR ui-copy-eight-locales；旧实现「非 en 一律中文」会让 ja/es/fr/de/ru/pt 用户看到中文）。</summary>
    /// <param name="locale">locale 标记。</param>
    /// <param name="expected">期望语言。</param>
    [Theory]
    [InlineData("en", UiLanguage.En)]
    [InlineData("en-GB", UiLanguage.En)]
    [InlineData("zh-CN", UiLanguage.Zh)]
    [InlineData("zh-Hans", UiLanguage.Zh)]
    [InlineData("ja-JP", UiLanguage.Ja)]
    [InlineData("es-ES", UiLanguage.Es)]
    [InlineData("fr-FR", UiLanguage.Fr)]
    [InlineData("de-DE", UiLanguage.De)]
    [InlineData("ru-RU", UiLanguage.Ru)]
    [InlineData("pt-BR", UiLanguage.Pt)]
    [InlineData("ko-KR", UiLanguage.En)]
    public void Language_ResolvesEightLocaleClosedSet(string locale, UiLanguage expected)
    {
        var ui = new UiLocale();
        ui.Set(locale);
        Assert.Equal(expected, ui.Language);
    }

    /// <summary>「没有 locale 来源」（null/空白）不是「未注册语言」：它取缺省语言单点，不是英文回落。
    /// 三处缺省面必须同值——<see cref="UiLanguageResolver.NoSource"/>、<see cref="UiLocale.DefaultLanguage"/>、
    /// 以及 <see cref="UiLocale"/> 无端口时的初始 locale（ADR ui-locale-no-source-single-source）。</summary>
    [Fact]
    public void NoSource_IsOneFactAcrossAllDefaults()
    {
        // 语言面：产品缺省是中文（钉值，不是钉委托）
        Assert.Equal(UiLanguage.Zh, UiLanguageResolver.NoSource);
        // 标记面 → 语言面：规范标记必须解析回同一语言（标记改成未注册形态会立刻红）
        Assert.Equal(UiLanguageResolver.NoSource, UiLanguageResolver.Resolve(UiLanguageResolver.NoSourceLocale));
        // 与单点同值：任一侧的语言值改成另一种语言即红。注：这条不判「委托关系」——
        // 把 DefaultLanguage 改回同值字面量仍会通过，委托本身没有可机器判据的观测点。
        Assert.Equal(UiLanguageResolver.NoSource, UiLocale.DefaultLanguage);
        // 「无来源」三个入口同答
        Assert.Equal(UiLanguageResolver.NoSource, UiLanguageResolver.Resolve(null));
        Assert.Equal(UiLanguageResolver.NoSource, UiLanguageResolver.Resolve(""));
        Assert.Equal(UiLanguageResolver.NoSource, UiLanguageResolver.Resolve("   "));
        // 生产路径：探测端口没接线 / 回空白，都落规范标记（BOM 在探测面就归零，见 OsLocaleProbeTests）
        Assert.Equal(UiLanguageResolver.NoSourceLocale, new UiLocale().Current);
        Assert.Equal(UiLanguageResolver.NoSourceLocale, new UiLocale(probe: new FakeProbe(null)).Current);
        Assert.Equal(UiLanguageResolver.NoSourceLocale, new UiLocale(probe: new FakeProbe("")).Current);
    }

    /// <summary>POSIX 形态的 locale（OS 探测面给的就是这一族：<c>LC_ALL</c>/<c>LANG</c> 的
    /// <c>zh_CN.UTF-8</c>/<c>ja_JP.UTF-8</c>）必须按主语言子标签落到对应语言——只认 BCP 47 的
    /// <c>-</c> 会把整串当主标签，把中文用户的宿主壳甩到英文回落。</summary>
    /// <param name="locale">POSIX 风格 locale 标记。</param>
    /// <param name="expected">期望语言。</param>
    [Theory]
    [InlineData("zh_CN.UTF-8", UiLanguage.Zh)]
    [InlineData("zh_CN", UiLanguage.Zh)]
    [InlineData("zh_TW.Big5", UiLanguage.Zh)]
    [InlineData("ja_JP.UTF-8", UiLanguage.Ja)]
    [InlineData("pt_BR.UTF-8", UiLanguage.Pt)]
    [InlineData("de_DE.UTF-8", UiLanguage.De)]
    [InlineData("en_US.UTF-8", UiLanguage.En)]
    [InlineData("ko_KR.UTF-8", UiLanguage.En)]
    public void Language_ResolvesPosixLocaleForms(string locale, UiLanguage expected)
    {
        var ui = new UiLocale();
        ui.Set(locale);
        Assert.Equal(expected, ui.Language);
    }

    /// <summary>非中文语言不得落回中文分支：六种新增语言全部走各自的字典。</summary>
    [Fact]
    public void Language_NonChineseLocales_NeverFallBackToChinese()
    {
        var ui = new UiLocale();
        foreach ((string tag, UiLanguage expected) in new[]
        {
            ("ja", UiLanguage.Ja), ("es", UiLanguage.Es), ("fr", UiLanguage.Fr),
            ("de", UiLanguage.De), ("ru", UiLanguage.Ru), ("pt-BR", UiLanguage.Pt),
        })
        {
            ui.Set(tag);
            Assert.Equal(expected, ui.Language);
            // 关键回归：托盘/横幅不再拿中文文案糊弄这些用户。
            Assert.NotEqual(UiCopy.TrayQuit(UiLanguage.Zh), UiCopy.TrayQuit(ui.Language));
        }
    }

    /// <summary>八语言字典逐条非空且互不相同（漏译会退化成同一条英文/中文串）。</summary>
    [Fact]
    public void UiCopy_AllEightLanguages_DistinctNonEmpty()
    {
        UiLanguage[] all = Enum.GetValues<UiLanguage>();
        foreach (UiLanguage lang in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(UiCopy.TrayShowMainWindow(lang)));
            Assert.False(string.IsNullOrWhiteSpace(UiCopy.RecoveryDialogTitle(lang)));
            Assert.False(string.IsNullOrWhiteSpace(UiCopy.WebView2MissingBody(lang)));
        }

        // 抽查三处：八种语言两两不同（同名串即漏译）。OkLabel 不入此列——日语「OK」与英文同形是
        // 正确译法而非漏译，语言间同形只能逐条判断，不能机械断言。
        Assert.Equal(8, all.Select(UiCopy.TrayQuit).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, all.Select(UiCopy.RecoveryExitButton).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, all.Select(UiCopy.RecoveryAutoRetryNote).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>列表分隔符八语言逐值钉住：全角分号仅 zh/ja，fr 用法式前置空格，其余为半角分号加空格。</summary>
    [Fact]
    public void ListSeparator_EachLanguageExactValue()
    {
        Assert.Equal("；", UiCopy.ListSeparator(UiLanguage.Zh));
        Assert.Equal("；", UiCopy.ListSeparator(UiLanguage.Ja));
        Assert.Equal(" ; ", UiCopy.ListSeparator(UiLanguage.Fr));
        foreach (UiLanguage lang in new[]
                 {
                     UiLanguage.En, UiLanguage.Es, UiLanguage.De, UiLanguage.Ru, UiLanguage.Pt,
                 })
        {
            Assert.Equal("; ", UiCopy.ListSeparator(lang));
        }
    }

    /// <summary>验证空白语言标记被拒绝并抛出 ArgumentException。</summary>
    [Fact]
    public void Set_NullOrWhitespace_Throws()
    {
        var ui = new UiLocale();
        Assert.Throws<ArgumentException>(() => ui.Set("  "));
    }

    /// <summary>验证新实例构造即有非空语言（OS locale 或 zh-CN 兜底），任何上报到达前宿主面即可用。</summary>
    [Fact]
    public void Default_IsOsFallback_NonEmpty()
    {
        // 构造即有缺省（OS locale 或 zh-CN），保证宿主面在任何上报到达前可用
        var ui = new UiLocale();
        Assert.False(string.IsNullOrWhiteSpace(ui.Current));
    }

    /// <summary>验证持久化端口有记录时构造即取该值——dsh 起来前（引导页）也能拿到上次 dsh 语言。</summary>
    [Fact]
    public void Constructor_PersistedLocale_Wins()
    {
        var ui = new UiLocale(new FakeStore("en"));

        Assert.Equal("en", ui.Current);
        Assert.Equal(UiLanguage.En, ui.Language);
    }

    /// <summary>验证持久值形态非法（坏文件内容）时不采信，回退 OS locale 而非原样使用。</summary>
    [Fact]
    public void Constructor_ImplausiblePersisted_FallsBackToOsLocale()
    {
        var ui = new UiLocale(new FakeStore("!! not a locale !!"));

        Assert.NotEqual("!! not a locale !!", ui.Current);
        Assert.False(string.IsNullOrWhiteSpace(ui.Current));
    }

    /// <summary>验证上报即落盘，且同值重复上报不重复写（值未变化即无状态变更）。</summary>
    [Fact]
    public void Set_PersistsOnChangeOnly()
    {
        var store = new FakeStore(null);
        var ui = new UiLocale(store);

        ui.Set("en");
        ui.Set("en");
        ui.Set("zh-CN");

        Assert.Equal(2, store.Saves);
        Assert.Equal("zh-CN", store.Saved);
    }

    /// <summary>验证形态判据（持久回读与 companion 上报共用）：合法语言标记集合与非法集合的边界。</summary>
    /// <param name="locale">待判定的语言标记。</param>
    /// <param name="expected">期望判定结果。</param>
    [Theory]
    [InlineData("en", true)]
    [InlineData("zh-CN", true)]
    [InlineData("en-GB", true)]
    [InlineData("", false)]
    [InlineData("e", false)]
    [InlineData("zh_CN", false)]
    [InlineData("zh CN", false)]
    [InlineData("en-", false)]
    public void IsPlausibleLocale_Boundaries(string locale, bool expected)
    {
        Assert.Equal(expected, UiLocale.IsPlausibleLocale(locale));
    }

    /// <summary>探测端口测试替身：固定回一个值（含 null / 只有 BOM 的退化形态）。</summary>
    private sealed class FakeProbe(string? detect) : IHostLocaleProbe
    {
        /// <inheritdoc />
        public string? Detect() => detect;
    }

    /// <summary>持久化端口测试替身：固定回读值 + 记录写入次数。</summary>
    private sealed class FakeStore(string? load) : IUiLocaleStore
    {
        /// <summary>最近一次写入值。</summary>
        public string? Saved { get; private set; }

        /// <summary>写入次数。</summary>
        public int Saves { get; private set; }

        /// <inheritdoc />
        public string? Load() => load;

        /// <inheritdoc />
        public void Save(string locale)
        {
            Saved = locale;
            Saves++;
        }
    }
}
