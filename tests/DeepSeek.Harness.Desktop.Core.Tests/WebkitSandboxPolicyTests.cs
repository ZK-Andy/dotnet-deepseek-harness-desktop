namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>WebKit 沙箱降级裁决（ADR webkit-sandbox-userns-fallback）：只认 sysctl 阳性信号，其余一律保持沙箱。</summary>
public class WebkitSandboxPolicyTests
{
    /// <summary>非 Linux 一律保持（文件内容再阳性也不看：bwrap 只存在于 Linux）。</summary>
    [Theory]
    [InlineData("0", "1")]
    [InlineData("1", "0")]
    [InlineData(null, null)]
    public void NonLinux_AlwaysKeeps(string? userns, string? apparmor)
    {
        Assert.Equal(WebkitSandboxPolicy.Disposition.KeepSandbox, WebkitSandboxPolicy.Evaluate(false, userns, apparmor));
    }

    /// <summary>userns 总开关为 0 → 禁用（含空白包裹形态）。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("0\n")]
    [InlineData("  0  ")]
    public void UsernsDisabled_Disables(string userns)
    {
        Assert.Equal(WebkitSandboxPolicy.Disposition.DisableSandbox, WebkitSandboxPolicy.Evaluate(true, userns, null));
    }

    /// <summary>AppArmor 限制为 1 → 禁用；双阳性同理（短路谁先命中不重要）。</summary>
    [Theory]
    [InlineData(null, "1\n")]
    [InlineData("0", "1")]
    public void ApparmorRestricted_Disables(string? userns, string apparmor)
    {
        Assert.Equal(WebkitSandboxPolicy.Disposition.DisableSandbox, WebkitSandboxPolicy.Evaluate(true, userns, apparmor));
    }

    /// <summary>健康主机 → 保持：开关开着、限制没开、双缺失、脏串一律不关沙箱。</summary>
    [Theory]
    [InlineData("1", null)]
    [InlineData(null, "0")]
    [InlineData("1", "0")]
    [InlineData(null, null)]
    [InlineData("yes", "no")]
    [InlineData("", "")]
    public void HealthyOrUnknown_Keeps(string? userns, string? apparmor)
    {
        Assert.Equal(WebkitSandboxPolicy.Disposition.KeepSandbox, WebkitSandboxPolicy.Evaluate(true, userns, apparmor));
    }
}
