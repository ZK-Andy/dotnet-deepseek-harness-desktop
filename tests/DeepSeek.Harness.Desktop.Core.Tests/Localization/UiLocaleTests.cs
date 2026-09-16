
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

    /// <summary>验证仅 en 前缀（en/en-GB）判定为英文，zh-CN 与 fr 等其余语言均不判定为英文。</summary>
    [Fact]
    public void IsEnglish_OnlyEnPrefix()
    {
        var ui = new UiLocale();
        ui.Set("en");
        Assert.True(ui.IsEnglish);
        ui.Set("en-GB");
        Assert.True(ui.IsEnglish);
        ui.Set("zh-CN");
        Assert.False(ui.IsEnglish);
        ui.Set("fr");
        Assert.False(ui.IsEnglish);
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
        Assert.True(ui.IsEnglish);
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
