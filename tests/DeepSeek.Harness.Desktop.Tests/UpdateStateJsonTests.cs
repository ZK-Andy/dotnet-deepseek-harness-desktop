using System.Text.Json;
using DeepSeek.Harness.Desktop.Services.Update;

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
        string json = new UpdateState(UpdateStatus.Ready, "0.2.0", Current: "0.1.20").ToJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal("ready", parsed.GetProperty("status").GetString());
        Assert.Equal("0.2.0", parsed.GetProperty("version").GetString());
        Assert.Equal("0.1.20", parsed.GetProperty("current").GetString());
    }

    [Fact]
    public void ToJson_Error_MessageEscapedAndRoundTrips()
    {
        const string reason = "下载失败 \"SHA256\" 不匹配";
        string json = new UpdateState(UpdateStatus.Error, Message: reason, Current: "0.1.20").ToJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(reason, parsed.GetProperty("message").GetString());
        Assert.Equal("0.1.20", parsed.GetProperty("current").GetString());
    }

    [Fact]
    public void ToJson_NullMessageAndCurrent_OmitFields()
    {
        string json = new UpdateState(UpdateStatus.Downloading, "9.9.9").ToJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.False(parsed.TryGetProperty("message", out _));
        Assert.False(parsed.TryGetProperty("current", out _));
    }

    [Fact]
    public void ToJson_HostileVersionAndCurrent_RoundTripAfterEscaping()
    {
        // 防御性转义：不依赖「上游已把版本号校验为数字段」的隐式约定
        const string hostile = "0.1.\"x\"";
        string json = new UpdateState(UpdateStatus.Ready, hostile, Current: hostile).ToJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(hostile, parsed.GetProperty("version").GetString());
        Assert.Equal(hostile, parsed.GetProperty("current").GetString());
    }
}
