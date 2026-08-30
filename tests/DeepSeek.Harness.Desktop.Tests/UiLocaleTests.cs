using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>宿主 UI 语言单点契约（ADR host-ui-locale）：归一化、变化事件、英文分支判定、OS 缺省。</summary>
public class UiLocaleTests
{
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

    [Fact]
    public void Set_NullOrWhitespace_Throws()
    {
        var ui = new UiLocale();
        Assert.Throws<ArgumentException>(() => ui.Set("  "));
    }

    [Fact]
    public void Default_IsOsFallback_NonEmpty()
    {
        // 构造即有缺省（OS locale 或 zh-CN），保证宿主面在任何上报到达前可用
        var ui = new UiLocale();
        Assert.False(string.IsNullOrWhiteSpace(ui.Current));
    }
}
