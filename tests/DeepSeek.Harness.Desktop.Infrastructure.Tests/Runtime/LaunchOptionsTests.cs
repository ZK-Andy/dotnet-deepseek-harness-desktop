namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>A 类启动配置解析契约：DevTools 开关随 <see cref="LaunchOptions.Resolve"/> 单点读 env——
/// 默认关、<c>=1</c> 开、其余值关，与 dev 判定相互独立；原组合根散读下沉后的行为钉
/// （ADR post-restructure-ledger-batch）。</summary>
[Collection("dsh-home-env")]
public class LaunchOptionsTests
{
    /// <summary>DevTools 只认 <see cref="LaunchOptions.DevToolsEnv"/>=1，任何其他值（含 null）关闭；
    /// env 读取归 Resolve 单点，组合根不再散读。</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("true", false)]
    public void Resolve_DevTools_FollowsEnvValue(string? envValue, bool expected)
    {
        string? original = Environment.GetEnvironmentVariable(LaunchOptions.DevToolsEnv);
        try
        {
            Environment.SetEnvironmentVariable(LaunchOptions.DevToolsEnv, envValue);

            var options = LaunchOptions.Resolve(_ => { });

            Assert.Equal(expected, options.DevTools);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LaunchOptions.DevToolsEnv, original);
        }
    }

    /// <summary>DevTools 开关与 dev 判定相互独立：无任何 dev 标记（两个触发器都中和）时 =1 仍开
    /// 调试器，不会因走产品语义分支而丢失。</summary>
    [Fact]
    public void Resolve_DevTools_IndependentOfDevFlag()
    {
        string? originalFlag = Environment.GetEnvironmentVariable(DevEnvironment.DevFlagEnv);
        string? originalRuntimeDir = Environment.GetEnvironmentVariable(DevEnvironment.RuntimeDirEnv);
        string? originalTools = Environment.GetEnvironmentVariable(LaunchOptions.DevToolsEnv);
        try
        {
            Environment.SetEnvironmentVariable(DevEnvironment.DevFlagEnv, null);
            Environment.SetEnvironmentVariable(DevEnvironment.RuntimeDirEnv, null);
            Environment.SetEnvironmentVariable(LaunchOptions.DevToolsEnv, "1");

            var options = LaunchOptions.Resolve(_ => { });

            Assert.True(options.DevTools);
            Assert.False(options.IsDev);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DevEnvironment.DevFlagEnv, originalFlag);
            Environment.SetEnvironmentVariable(DevEnvironment.RuntimeDirEnv, originalRuntimeDir);
            Environment.SetEnvironmentVariable(LaunchOptions.DevToolsEnv, originalTools);
        }
    }
}
