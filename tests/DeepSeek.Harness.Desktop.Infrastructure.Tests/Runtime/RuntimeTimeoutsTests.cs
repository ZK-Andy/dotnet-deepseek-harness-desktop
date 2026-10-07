
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>运行时超时家装载（ADR timeout-hardcode-to-appsettings）：默认值锁定 = 同值迁移的零行为变更证明；
/// 缺节/坏 JSON/逐键覆盖/类型不符回退与 UpdateOptions 同哲学。</summary>
public class RuntimeTimeoutsTests
{
    /// <summary>验证全部 29 项默认值与收归前字面量一致：任一漂移即行为变更，测试先红。</summary>
    [Fact]
    public void Defaults_MatchPreMigrationLiterals()
    {
        var o = new RuntimeTimeouts();

        Assert.Equal(1, o.RelayWaitIntervalSeconds);
        Assert.Equal(2, o.RelayHelperGraceSeconds);
        Assert.Equal(2, o.RelayReadyStableWindowSeconds);
        Assert.Equal(1, o.AdoptedExitPollIntervalSeconds);
        Assert.Equal(500, o.PortProbeTimeoutMilliseconds);
        Assert.Equal(1500, o.WebProbeTimeoutMilliseconds);
        Assert.Equal(3, o.LifecycleGateWaitSeconds);
        Assert.Equal(60, o.SpawnTimeoutSeconds);
        Assert.Equal(2, o.NotifyPrimaryTimeoutSeconds);
        Assert.Equal(2, o.SupervisorJoinTimeoutSeconds);
        Assert.Equal(60, o.SupervisorRestartTimeoutSeconds);
        Assert.Equal(2, o.SupervisorRecoveredRetryDelaySeconds);
        Assert.Equal(1, o.SupervisorFailedRetryDelaySeconds);
        Assert.Equal(10, o.HealthInitialDelaySeconds);
        Assert.Equal(120, o.BootstrapSettleTimeoutSeconds);
        Assert.Equal(5, o.NavCommitTimeoutSeconds);
        Assert.Equal(30, o.NavCallTimeoutSeconds);
        Assert.Equal(15, o.AuthProbeTimeoutSeconds);
        Assert.Equal(2, o.AuthProbeAttempts);
        Assert.Equal(120, o.WindowReadyTimeoutSeconds);
        Assert.Equal(1, o.WindowReadyPollIntervalSeconds);
        Assert.Equal(5, o.IpcServeTimeoutSeconds);
        Assert.Equal(1, o.IpcAcceptRetryDelaySeconds);
        Assert.Equal(8, o.VersionProbeTimeoutSeconds);
        Assert.Equal(30, o.BannerMaxAttempts);
        Assert.Equal(1, o.BannerRetryDelaySeconds);
        Assert.Equal(15, o.PushMaxAttempts);
        Assert.Equal(400, o.PushRetryDelayMilliseconds);
        Assert.Equal(60, o.CaptionBarRegisterSeconds);
    }

    /// <summary>验证无 RuntimeTimeouts 键或节非对象时返回全默认值。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"RuntimeTimeouts":null}""")]
    [InlineData("""{"RuntimeTimeouts":[]}""")]
    [InlineData("""{"Other":1}""")]
    public void Parse_MissingSection_ReturnsDefaults(string json)
    {
        var o = RuntimeTimeouts.Parse(json);

        Assert.Equal(1, o.RelayWaitIntervalSeconds);
        Assert.Equal(500, o.PortProbeTimeoutMilliseconds);
        Assert.Equal(60, o.SpawnTimeoutSeconds);
        Assert.Equal(30, o.BannerMaxAttempts);
        Assert.Equal(400, o.PushRetryDelayMilliseconds);
    }

    /// <summary>验证坏 JSON 由 Parse 抛 JsonException——Load 调用方的 catch 兜底转全默认，
    /// 纯函数面保持「损坏即抛」契约（与 UpdateOptions.Parse 同分界）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    public void Parse_BrokenJson_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => RuntimeTimeouts.Parse(json));
    }

    /// <summary>验证合法节全键覆盖：每键非常值，断言逐项生效（含探针毫秒与推送毫秒单位）。</summary>
    [Fact]
    public void Parse_ValidSection_OverridesAllKeys()
    {
        var o = RuntimeTimeouts.Parse("""
            {"RuntimeTimeouts":{
            "RelayWaitIntervalSeconds":3,"RelayHelperGraceSeconds":4,"RelayReadyStableWindowSeconds":5,
            "AdoptedExitPollIntervalSeconds":6,"PortProbeTimeoutMilliseconds":700,"WebProbeTimeoutMilliseconds":800,
            "LifecycleGateWaitSeconds":9,"SpawnTimeoutSeconds":61,"NotifyPrimaryTimeoutSeconds":11,
            "SupervisorJoinTimeoutSeconds":12,"SupervisorRestartTimeoutSeconds":62,
            "SupervisorRecoveredRetryDelaySeconds":13,"SupervisorFailedRetryDelaySeconds":14,
            "HealthInitialDelaySeconds":15,"BootstrapSettleTimeoutSeconds":16,"NavCommitTimeoutSeconds":17,"NavCallTimeoutSeconds":31,
            "AuthProbeTimeoutSeconds":25,"AuthProbeAttempts":28,"WindowReadyTimeoutSeconds":26,"WindowReadyPollIntervalSeconds":27,
            "IpcServeTimeoutSeconds":18,"IpcAcceptRetryDelaySeconds":19,
            "VersionProbeTimeoutSeconds":20,"BannerMaxAttempts":21,"BannerRetryDelaySeconds":22,
            "PushMaxAttempts":23,"PushRetryDelayMilliseconds":24,"CaptionBarRegisterSeconds":29}}
            """);

        Assert.Equal(3, o.RelayWaitIntervalSeconds);
        Assert.Equal(4, o.RelayHelperGraceSeconds);
        Assert.Equal(5, o.RelayReadyStableWindowSeconds);
        Assert.Equal(6, o.AdoptedExitPollIntervalSeconds);
        Assert.Equal(700, o.PortProbeTimeoutMilliseconds);
        Assert.Equal(800, o.WebProbeTimeoutMilliseconds);
        Assert.Equal(9, o.LifecycleGateWaitSeconds);
        Assert.Equal(61, o.SpawnTimeoutSeconds);
        Assert.Equal(11, o.NotifyPrimaryTimeoutSeconds);
        Assert.Equal(12, o.SupervisorJoinTimeoutSeconds);
        Assert.Equal(62, o.SupervisorRestartTimeoutSeconds);
        Assert.Equal(13, o.SupervisorRecoveredRetryDelaySeconds);
        Assert.Equal(14, o.SupervisorFailedRetryDelaySeconds);
        Assert.Equal(15, o.HealthInitialDelaySeconds);
        Assert.Equal(16, o.BootstrapSettleTimeoutSeconds);
        Assert.Equal(17, o.NavCommitTimeoutSeconds);
        Assert.Equal(31, o.NavCallTimeoutSeconds);
        Assert.Equal(25, o.AuthProbeTimeoutSeconds);
        Assert.Equal(28, o.AuthProbeAttempts);
        Assert.Equal(26, o.WindowReadyTimeoutSeconds);
        Assert.Equal(27, o.WindowReadyPollIntervalSeconds);
        Assert.Equal(18, o.IpcServeTimeoutSeconds);
        Assert.Equal(19, o.IpcAcceptRetryDelaySeconds);
        Assert.Equal(20, o.VersionProbeTimeoutSeconds);
        Assert.Equal(21, o.BannerMaxAttempts);
        Assert.Equal(22, o.BannerRetryDelaySeconds);
        Assert.Equal(23, o.PushMaxAttempts);
        Assert.Equal(24, o.PushRetryDelayMilliseconds);
        Assert.Equal(29, o.CaptionBarRegisterSeconds);
    }

    /// <summary>验证部分键覆盖时其余键保持默认；类型不符的键回退默认不影响其余键。</summary>
    [Fact]
    public void Parse_PartialAndMismatch_KeepsDefaultsForMissingKeys()
    {
        var o = RuntimeTimeouts.Parse("""
            {"RuntimeTimeouts":{"SpawnTimeoutSeconds":61,"PortProbeTimeoutMilliseconds":"700","BannerMaxAttempts":21}}
            """);

        Assert.Equal(61, o.SpawnTimeoutSeconds);
        Assert.Equal(500, o.PortProbeTimeoutMilliseconds);
        Assert.Equal(21, o.BannerMaxAttempts);
        Assert.Equal(1, o.RelayWaitIntervalSeconds);
    }

    /// <summary>验证根非对象（数组/字符串/数字/null）时回退默认而非抛错——该形态下 TryGetProperty 抛
    /// InvalidOperationException，曾穿透只 catch JsonException 的 Load 在启动早期即崩（2026-10-06 R1/R2 挂账，
    /// ADR config-load-fail-safe-and-symlink-privilege-fallback）。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Parse_RootNotObject_FallsBackToDefaults(string json)
    {
        var o = RuntimeTimeouts.Parse(json);

        Assert.Equal(1, o.RelayWaitIntervalSeconds);
        Assert.Equal(60, o.SpawnTimeoutSeconds);
        Assert.Equal(400, o.PushRetryDelayMilliseconds);
    }

    /// <summary>验证数值键不可表示（小数/超 Int32 范围）时该键回退默认、其余键照常生效——
    /// GetInt32 对小数与越界抛 FormatException，曾穿透只 catch JsonException 的 Load（同上 ADR）。</summary>
    [Fact]
    public void Parse_UnrepresentableNumber_KeepsDefaultForThatKey()
    {
        var o = RuntimeTimeouts.Parse("""
            {"RuntimeTimeouts":{"SpawnTimeoutSeconds":1.5,"BannerMaxAttempts":99999999999,"RelayWaitIntervalSeconds":3}}
            """);

        Assert.Equal(60, o.SpawnTimeoutSeconds);
        Assert.Equal(30, o.BannerMaxAttempts);
        Assert.Equal(3, o.RelayWaitIntervalSeconds);
    }

    /// <summary>验证坏 JSON 文件时 Load fail-safe 回退默认值而非抛异常，配置损坏不阻塞启动。</summary>
    [Fact]
    public void Load_BrokenJson_FailsSafeToDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rtimeouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{not json");
            var options = RuntimeTimeouts.Load(dir);
            Assert.Equal(60, options.SpawnTimeoutSeconds);
            Assert.Equal(500, options.PortProbeTimeoutMilliseconds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证缺文件时 Load 返回全默认值（沙箱/单测基目录无 appsettings 即此路径）。</summary>
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rtimeouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = RuntimeTimeouts.Load(dir);
            Assert.Equal(60, options.SpawnTimeoutSeconds);
            Assert.Equal(30, options.BannerMaxAttempts);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
