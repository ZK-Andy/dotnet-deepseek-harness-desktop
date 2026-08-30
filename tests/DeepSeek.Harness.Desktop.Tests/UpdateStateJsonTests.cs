using System.Text.Json;
using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>UpdateState.ToJson 页面契约：current/message 字段的形状与转义（设置页更新区数据源）。</summary>
public class UpdateStateJsonTests
{
    /// <summary>验证 idle 状态序列化为紧凑 JSON 时仅含 status 字段且 version 显式为 null。</summary>
    [Fact]
    public void ToJson_Minimal_IdleHasNullVersionOnly()
    {
        Assert.Equal("""{"status":"idle","version":null}""", new UpdateState(UpdateStatus.Idle).ToJson());
    }

    /// <summary>验证 ready 状态序列化后 status/version/current 三字段齐全且取值正确。</summary>
    [Fact]
    public void ToJson_Ready_IncludesVersionAndCurrent()
    {
        string json = new UpdateState(UpdateStatus.Ready, "0.2.0", Current: "0.1.20").ToJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal("ready", parsed.GetProperty("status").GetString());
        Assert.Equal("0.2.0", parsed.GetProperty("version").GetString());
        Assert.Equal("0.1.20", parsed.GetProperty("current").GetString());
    }

    /// <summary>验证含引号等特殊字符的 message 被正确转义，反序列化后与原文逐字一致。</summary>
    [Fact]
    public void ToJson_Error_MessageEscapedAndRoundTrips()
    {
        const string reason = "下载失败 \"SHA256\" 不匹配";
        string json = new UpdateState(UpdateStatus.Error, Message: reason, Current: "0.1.20").ToJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(reason, parsed.GetProperty("message").GetString());
        Assert.Equal("0.1.20", parsed.GetProperty("current").GetString());
    }

    /// <summary>验证 message/current 为 null 时对应字段整体省略，不输出 null 占位。</summary>
    [Fact]
    public void ToJson_NullMessageAndCurrent_OmitFields()
    {
        string json = new UpdateState(UpdateStatus.Downloading, "9.9.9").ToJson();
        JsonElement parsed = JsonDocument.Parse(json).RootElement;
        Assert.False(parsed.TryGetProperty("message", out _));
        Assert.False(parsed.TryGetProperty("current", out _));
    }

    /// <summary>验证版本串含引号等敌意字符时仍被转义、往返解析无损，不依赖上游格式校验。</summary>
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
