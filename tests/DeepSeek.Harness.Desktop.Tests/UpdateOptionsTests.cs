using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>自更新栈装载门禁：非 dev 恒真；dev 需显式 FORCE 开关（审核加固，防 dev 装系统包后重启 dev 二进制）。</summary>
public class UpdateOptionsTests
{
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