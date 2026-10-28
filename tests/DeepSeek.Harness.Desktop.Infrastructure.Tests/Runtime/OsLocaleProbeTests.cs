namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>
/// 宿主 locale 探测的三级回退（环境变量族 → CurrentUICulture → 缺省语言单点）：读环境变量是进程面，
/// 故注入假读取来验（不改全局环境变量，避免与并行用例互相干扰）。
/// </summary>
public class OsLocaleProbeTests
{
    /// <summary>验证 LC_ALL 优先（首个有值的键即返回）。</summary>
    [Fact]
    public void Detect_PrefersFirstNonEmptyEnvVar()
    {
        var probe = new OsLocaleProbe(
            env: key => key switch { "LC_ALL" => "fr_FR.UTF-8", _ => null },
            currentCultureName: () => "en-US");

        // POSIX 形态归一到 BCP 47：语言单点与页面 html[lang] 都按 BCP 47 消费。
        Assert.Equal("fr-FR", probe.Detect());
    }

    /// <summary>验证 LC_ALL 空值时顺延到 LC_MESSAGES / LANG。</summary>
    [Fact]
    public void Detect_FallsThroughEnvVarsInOrder()
    {
        var probe = new OsLocaleProbe(
            env: key => key switch { "LC_ALL" => "  ", "LC_MESSAGES" => "de_DE", _ => null },
            currentCultureName: () => "en-US");

        Assert.Equal("de-DE", probe.Detect());
    }

    /// <summary>验证环境变量族全空时回 CurrentUICulture。</summary>
    [Fact]
    public void Detect_FallsBackToCurrentCulture()
    {
        var probe = new OsLocaleProbe(env: _ => null, currentCultureName: () => "en-US");

        Assert.Equal("en-US", probe.Detect());
    }

    /// <summary>POSIX 环境变量族的归一：修饰符剥掉、<c>_</c> 换 <c>-</c>；<c>C</c>/<c>POSIX</c>
    /// 是「未设置」的正式取值而非语言，原样交给消费方按未注册语言回落。</summary>
    /// <param name="envValue">环境变量原值。</param>
    /// <param name="expected">期望的 BCP 47 形态。</param>
    [Theory]
    [InlineData("zh_CN.UTF-8", "zh-CN")]
    [InlineData("zh_TW.Big5", "zh-TW")]
    [InlineData("ja_JP.UTF-8", "ja-JP")]
    [InlineData("zh_CN", "zh-CN")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("C", "C")]
    [InlineData("POSIX", "POSIX")]
    [InlineData("en_US.utf8", "en-US")]
    public void Detect_NormalizesPosixToBcp47(string envValue, string expected)
    {
        var probe = new OsLocaleProbe(
            env: key => key switch { "LANG" => envValue, _ => null },
            currentCultureName: () => "en-US");

        Assert.Equal(expected, probe.Detect());
    }

    /// <summary>验证 CurrentUICulture 为空串时回缺省语言单点（产品缺省＝中文，标记见
    /// <see cref="UiLanguageResolver.NoSourceLocale"/>）：本行是生产启动路径真正走到的「无来源」缺省。</summary>
    [Fact]
    public void Detect_EmptyCultureName_FallsBackToNoSource()
    {
        var probe = new OsLocaleProbe(env: _ => null, currentCultureName: () => string.Empty);

        Assert.Equal(UiLanguageResolver.NoSourceLocale, probe.Detect());
    }

    /// <summary>环境变量带 BOM（Windows 侧工具写过的痕迹）时按 BOM 剥离后归一，不得把 BOM 带进主语言子标签。</summary>
    [Fact]
    public void Detect_BomPrefixedEnvValue_StripsBom()
    {
        var probe = new OsLocaleProbe(
            env: key => key switch { "LC_ALL" => "\uFEFFzh_CN.UTF-8", _ => null },
            currentCultureName: () => "en-US");

        Assert.Equal("zh-CN", probe.Detect());
    }

    /// <summary>只有 BOM 的取值等于未设置：顺延下一个键，最终落 CurrentUICulture，不把空标记当 locale 传出。</summary>
    [Fact]
    public void Detect_BomOnlyEnvValue_FallsThrough()
    {
        var probe = new OsLocaleProbe(
            env: key => key switch { "LC_ALL" => "\uFEFF", _ => null },
            currentCultureName: () => "ja-JP");

        Assert.Equal("ja-JP", probe.Detect());
    }

    /// <summary>验证 CurrentUICulture 读取抛 ArgumentException（非法中性文化名）时回缺省语言单点，不外抛。</summary>
    [Fact]
    public void Detect_InvalidCulture_Throws_NotPropagated()
    {
        var probe = new OsLocaleProbe(
            env: _ => null,
            currentCultureName: () => throw new ArgumentException("invalid culture"));

        Assert.Equal(UiLanguageResolver.NoSourceLocale, probe.Detect());
    }
}
