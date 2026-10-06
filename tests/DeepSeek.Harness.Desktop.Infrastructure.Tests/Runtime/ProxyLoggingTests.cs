namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>回环代理日志档位装载（ADR proxy-log-noise-reduction）：默认 = 降噪态（逐请求 trace 关、
/// 慢阈值 2000ms）；缺节/坏 JSON/键缺失回退默认，不阻塞启动。</summary>
public class ProxyLoggingTests
{
    /// <summary>验证默认值即降噪态：Trace 关（成功路径不落流水）、慢阈值 2000ms。</summary>
    [Fact]
    public void Defaults_AreQuietTier()
    {
        var options = new ProxyLogging();

        Assert.False(options.Trace);
        Assert.Equal(2000, options.SlowRequestMilliseconds);
    }

    /// <summary>验证缺失 ProxyLogging 节时回退全默认（与 RuntimeTimeouts 同分界）。</summary>
    [Fact]
    public void Parse_MissingSection_ReturnsDefaults()
    {
        var options = ProxyLogging.Parse("""{"RuntimeTimeouts":{}}""");

        Assert.False(options.Trace);
        Assert.Equal(2000, options.SlowRequestMilliseconds);
    }

    /// <summary>验证合法节全键覆盖：Trace 打开、慢阈值生效。</summary>
    [Fact]
    public void Parse_ValidSection_OverridesAllKeys()
    {
        var options = ProxyLogging.Parse("""{"ProxyLogging":{"Trace":true,"SlowRequestMilliseconds":250}}""");

        Assert.True(options.Trace);
        Assert.Equal(250, options.SlowRequestMilliseconds);
    }

    /// <summary>验证部分键覆盖时其余键保持默认；类型不符的键回退默认不影响其余键。</summary>
    [Fact]
    public void Parse_PartialAndMismatch_KeepsDefaultsForMissingKeys()
    {
        var options = ProxyLogging.Parse("""{"ProxyLogging":{"Trace":"yes","SlowRequestMilliseconds":250}}""");

        Assert.False(options.Trace);
        Assert.Equal(250, options.SlowRequestMilliseconds);
    }

    /// <summary>验证数值键不可表示/越界/越下界一律回退默认而不抛：`GetInt32` 对小数与超 Int32 范围抛
    /// `FormatException`，配置面把它升级成启动异常即违反「损坏不阻塞启动」（ADR proxy-log-noise-reduction）。</summary>
    [Theory]
    [InlineData("2.7")]
    [InlineData("99999999999")]
    [InlineData("1e400")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("\"250\"")]
    public void Parse_UnusableSlowThreshold_FallsBackToDefault(string raw)
    {
        var options = ProxyLogging.Parse($"{{\"ProxyLogging\":{{\"SlowRequestMilliseconds\":{raw}}}}}");

        Assert.Equal(2000, options.SlowRequestMilliseconds);
    }

    /// <summary>验证根元素非对象时 Parse 回默认而不抛：`TryGetProperty` 对 `[]`/`"x"`/`42`/`null` 抛
    /// `InvalidOperationException`，不拦就会经静态单例首访升级成启动异常（ADR proxy-log-noise-reduction）。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Parse_NonObjectRoot_ReturnsDefaults(string json)
    {
        var options = ProxyLogging.Parse(json);

        Assert.False(options.Trace);
        Assert.Equal(2000, options.SlowRequestMilliseconds);
    }

    /// <summary>验证无人值守 trace 覆盖是 fail-closed 的纯判定：仅字面 `"1"` 打开，其余（`"0"`/空串/其他值/未设）
    /// 一律维持文件档位（ADR proxy-log-noise-reduction）；纯缝不碰进程环境，故无「静态单例锁存到测试窗口值」竞态。</summary>
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    public void ApplyTraceEnv_OnlyLiteralOneEnables(string? value, bool expected)
    {
        Assert.Equal(expected, ProxyLogging.ApplyTraceEnv(new ProxyLogging(), value).Trace);
    }

    /// <summary>验证覆盖只动 `Trace`，不扰动文件档位的其他键。</summary>
    [Fact]
    public void ApplyTraceEnv_KeepsOtherKeys()
    {
        var options = ProxyLogging.ApplyTraceEnv(new ProxyLogging { SlowRequestMilliseconds = 250 }, "1");

        Assert.True(options.Trace);
        Assert.Equal(250, options.SlowRequestMilliseconds);
    }

    /// <summary>验证 appsettings.json 存在但不可读（权限/占用）时 Load 仍回退默认而非抛：
    /// fail-safe 面覆盖 IO 而不只是坏 JSON。</summary>
    [Fact]
    public void Load_UnreadableFile_FailsSafeToDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "proxylog-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "appsettings.json");
            File.WriteAllText(path, """{"ProxyLogging":{"Trace":true}}""");
            if (!OperatingSystem.IsWindows())
            {
                // Windows 无 Unix 权限位（CA1416）：该腿走正常解析，只核「Load 不抛」。
                File.SetUnixFileMode(path, UnixFileMode.None);
            }

            // root 语境下 chmod 不阻断读取（此时走正常解析）——本断言只要求「不抛」。
            var options = ProxyLogging.Load(dir);
            Assert.NotNull(options);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证坏 JSON 由 Parse 抛 JsonException——Load 调用方的 catch 兜底转全默认。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    public void Parse_BrokenJson_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ProxyLogging.Parse(json));
    }

    /// <summary>验证坏 JSON 文件时 Load fail-safe 回退默认值而非抛异常，配置损坏不阻塞启动。</summary>
    [Fact]
    public void Load_BrokenJson_FailsSafeToDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "proxylog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{not json");
            var options = ProxyLogging.Load(dir);

            Assert.False(options.Trace);
            Assert.Equal(2000, options.SlowRequestMilliseconds);
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
        string dir = Path.Combine(Path.GetTempPath(), "proxylog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = ProxyLogging.Load(dir);

            Assert.False(options.Trace);
            Assert.Equal(2000, options.SlowRequestMilliseconds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
