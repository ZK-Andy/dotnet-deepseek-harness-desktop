using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>自更新栈装载门禁：非 dev 恒真；dev 需显式 FORCE 开关（审核加固，防 dev 装系统包后重启 dev 二进制）。</summary>
public class UpdateOptionsTests
{
    /// <summary>验证非 dev 恒启用；dev 仅在 FORCE=1 时启用，FORCE 为 null、空串或 0 均禁用（防 dev 装系统包后重启 dev 二进制）。</summary>
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(false, "1", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]
    public void IsEnabledFor_GatesOnlyDevWithoutForce(bool isDev, string? forceEnv, bool expected)
    {
        Assert.Equal(expected, UpdateOptions.IsEnabledFor(isDev, forceEnv));
    }
}

/// <summary>appsettings.json 的 Update 节 JSON→Options 解析（纯函数）：缺节/坏 JSON/逐键覆盖/类型不符回退。</summary>
public class UpdateOptionsParseTests
{
    /// <summary>验证无 Update 键或节为 null 时返回全默认值（空对象属合法 JSON 无节；null 节非对象回退）。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Update":null}""")]
    [InlineData("""{"Other":1}""")]
    public void Parse_MissingUpdateSection_ReturnsDefaults(string json)
    {
        var o = UpdateOptions.Parse(json);

        Assert.Equal("ZK-Andy/dotnet-deepseek-harness-desktop", o.Repository);
        Assert.Equal(15, o.FeedTimeoutSeconds);
        Assert.Equal(30, o.DownloadTimeoutMinutes);
        Assert.Equal("updates", o.UpdatesDirName);
    }

    /// <summary>验证坏 JSON（空串/非 JSON 文本）由 Parse 抛 JsonException（含 JsonReaderException 派生）——Load 调用方的
    /// catch 兜底转全默认，纯函数面保持「损坏即抛」契约（fail loud 与 fail-safe 的分界在 IO 边界）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    public void Parse_BrokenJson_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => UpdateOptions.Parse(json));
    }

    /// <summary>验证 Update 节非对象（数组/字符串/数字）时回退默认而非抛错。</summary>
    [Theory]
    [InlineData("""{"Update":[]}""")]
    [InlineData("""{"Update":"x"}""")]
    [InlineData("""{"Update":5}""")]
    public void Parse_UpdateSectionNotObject_FallsBackToDefaults(string json)
    {
        var o = UpdateOptions.Parse(json);

        Assert.Equal("ZK-Andy/dotnet-deepseek-harness-desktop", o.Repository);
        Assert.Equal(15, o.FeedTimeoutSeconds);
    }

    /// <summary>验证合法 Update 节逐键覆盖默认值：仓库/feed 秒数/下载分钟/更新目录名。</summary>
    [Fact]
    public void Parse_ValidSection_OverridesAllKeys()
    {
        var o = UpdateOptions.Parse("""
            {"Update":{"Repository":"me/mirror","FeedTimeoutSeconds":7,"DownloadTimeoutMinutes":45,"UpdatesDirName":"dl"}}
            """);

        Assert.Equal("me/mirror", o.Repository);
        Assert.Equal(7, o.FeedTimeoutSeconds);
        Assert.Equal(45, o.DownloadTimeoutMinutes);
        Assert.Equal("dl", o.UpdatesDirName);
    }

    /// <summary>验证类型不符（数值键给了字符串/字符串键给了数值）时该键回退默认，不影响其余键。</summary>
    [Theory]
    [InlineData("""{"Update":{"Repository":5,"FeedTimeoutSeconds":"7"}}""")]
    [InlineData("""{"Update":{"DownloadTimeoutMinutes":true,"UpdatesDirName":9}}""")]
    public void Parse_TypeMismatch_KeepsDefaultForThatKey(string json)
    {
        var o = UpdateOptions.Parse(json);

        Assert.Equal("ZK-Andy/dotnet-deepseek-harness-desktop", o.Repository);
        Assert.Equal(15, o.FeedTimeoutSeconds);
        Assert.Equal(30, o.DownloadTimeoutMinutes);
        Assert.Equal("updates", o.UpdatesDirName);
    }
}
