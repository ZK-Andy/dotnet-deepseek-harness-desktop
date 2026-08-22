using System.Text.Json;
using DeepSeek.Harness.Desktop.Services.Update;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>UpdateState.ToJson 页面契约：current/message 字段的形状与转义（设置页更新区数据源）。</summary>
public class UpdateStateJsonTests
{
    [Fact]
    public void ToJson_Minimal_IdleHasNullVersionOnly()
    {
        Assert.Equal("""{"status":"idle","version":null}""", new UpdateState(UpdateStatus.Idle).ToJson());
    }

    [Fact]
    public void ToJson_Ready_IncludesVersionAndCurrent()
    {
        var json = new UpdateState(UpdateStatus.Ready, "0.2.0", Current: "0.1.20").ToJson();
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal("ready", parsed.GetProperty("status").GetString());
        Assert.Equal("0.2.0", parsed.GetProperty("version").GetString());
        Assert.Equal("0.1.20", parsed.GetProperty("current").GetString());
    }

    [Fact]
    public void ToJson_Error_MessageEscapedAndRoundTrips()
    {
        const string reason = "下载失败 \"SHA256\" 不匹配";
        var json = new UpdateState(UpdateStatus.Error, Message: reason, Current: "0.1.20").ToJson();
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(reason, parsed.GetProperty("message").GetString());
        Assert.Equal("0.1.20", parsed.GetProperty("current").GetString());
    }

    [Fact]
    public void ToJson_NullMessageAndCurrent_OmitFields()
    {
        var json = new UpdateState(UpdateStatus.Downloading, "9.9.9").ToJson();
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.False(parsed.TryGetProperty("message", out _));
        Assert.False(parsed.TryGetProperty("current", out _));
    }
}
