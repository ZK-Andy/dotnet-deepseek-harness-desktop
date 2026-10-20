using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>appsettings 节装卸共享缝（解析半边）：节形态防御与逐键容错覆盖（三类选项记录共用此缝）。</summary>
public class ConfigSectionJsonTests
{
    /// <summary>验证节存在且为对象时逐键取值生效（数值与布尔各取本节值）。</summary>
    [Fact]
    public void Parse_SectionPresent_AppliesSectionValues()
    {
        int height = 0;
        bool trace = false;
        ConfigSectionJson.Parse("""{"Update":{"n":7,"flag":true}}""", "Update", section =>
        {
            height = ConfigSectionJson.GetInt(section, "n", 3);
            trace = ConfigSectionJson.GetBool(section, "flag", false);
            return 0;
        }, 0);

        Assert.Equal(7, height);
        Assert.True(trace);
    }

    /// <summary>验证根非对象、节缺失、节非对象三种形态损坏一律回退缺省（绝不升级成启动异常）。</summary>
    [Fact]
    public void Parse_BrokenShapes_FallBackToDefault()
    {
        Assert.Equal(42, ConfigSectionJson.Parse("[]", "Update", _ => 0, 42));
        Assert.Equal(42, ConfigSectionJson.Parse("""{"Other":{}}""", "Update", _ => 0, 42));
        Assert.Equal(42, ConfigSectionJson.Parse("""{"Update":"not-an-object"}""", "Update", _ => 0, 42));
    }

    /// <summary>验证逐键容错：非数值/越界的数值键回退缺省值，非布尔的布尔键不被误判为真。</summary>
    [Fact]
    public void GetInt_And_GetBool_BrokenValues_FallBackPerKey()
    {
        using var doc = JsonDocument.Parse("""{"s":"x","big":9999999999,"flag":true,"f":"yes"}""");
        JsonElement section = doc.RootElement;

        Assert.Equal(5, ConfigSectionJson.GetInt(section, "s", 5));
        Assert.Equal(5, ConfigSectionJson.GetInt(section, "big", 5));
        Assert.Equal(5, ConfigSectionJson.GetInt(section, "missing", 5));
        Assert.True(ConfigSectionJson.GetBool(section, "flag", false));
        Assert.False(ConfigSectionJson.GetBool(section, "f", false));
    }
}
