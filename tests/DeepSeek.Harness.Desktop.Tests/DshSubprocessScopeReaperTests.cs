using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 残留 dsh 下游 scope 收割（DshSubprocessScopeReaper）决策契约：owner dsh pid 已死的 scope 才
/// stop；命名形状不符 / owner 仍活 / stop 失败一律不误伤（零误杀，ADR dsh-sandbox-child-orphan-leak）。
/// </summary>
public class DshSubprocessScopeReaperTests
{
    /// <summary>收割成功路径的日志断言不依赖内容——统一静默消费。</summary>
    private static void NoLog(string _) { }

    /// <summary>验证合法 scope 名解析出内嵌的 owner dsh pid。</summary>
    [Theory]
    [InlineData("dsh-subprocess-9884-0a8e0167e46c.scope", 9884)]
    [InlineData("dsh-subprocess-1-deadbeef.scope", 1)]
    public void TryParseOwnerPid_ValidShape_ParsesOwnerPid(string unit, int expected)
    {
        Assert.True(DshSubprocessScopeReaper.TryParseOwnerPid(unit, out int pid));
        Assert.Equal(expected, pid);
    }

    /// <summary>验证非法形状（前缀/后缀/hash 段不符）与 pid 溢出都解析失败——调用方按不收割处理。</summary>
    [Theory]
    [InlineData("dsh-subprocess-9884.scope")]
    [InlineData("dsh-subprocess-9884-XYZ.scope")]
    [InlineData("dsh-subprocess--0a8e.scope")]
    [InlineData("other-subprocess-9884-0a8e.scope")]
    [InlineData("dsh-subprocess-9884-0a8e.scope.bak")]
    [InlineData("dsh-subprocess-99999999999999-0a8e.scope")]
    [InlineData("")]
    public void TryParseOwnerPid_InvalidShape_Fails(string unit)
    {
        Assert.False(DshSubprocessScopeReaper.TryParseOwnerPid(unit, out int pid));
        Assert.Equal(0, pid);
    }

    /// <summary>验证 owner 已死的 scope 被 stop 且计入成功数。</summary>
    [Fact]
    public void Reap_OwnerDead_StopsScope()
    {
        var stopped = new List<string>();

        int reaped = DshSubprocessScopeReaper.Reap(
            ["dsh-subprocess-9884-0a8e0167e46c.scope"],
            isOwnerDead: pid => pid == 9884,
            stopScope: unit => stopped.Add(unit),
            log: NoLog);

        Assert.Equal(1, reaped);
        Assert.Equal(["dsh-subprocess-9884-0a8e0167e46c.scope"], stopped);
    }

    /// <summary>验证 owner 仍活（另一实例在管或 pid 复用）的 scope 不被动（零误杀核心）。</summary>
    [Fact]
    public void Reap_OwnerAlive_DoesNotStop()
    {
        var stopped = new List<string>();

        int reaped = DshSubprocessScopeReaper.Reap(
            ["dsh-subprocess-61299-0a8e0167e46c.scope", "dsh-subprocess-9884-0a8e0167e46c.scope"],
            isOwnerDead: pid => pid != 61299,
            stopScope: unit => stopped.Add(unit),
            log: NoLog);

        Assert.Equal(1, reaped);
        Assert.Equal(["dsh-subprocess-9884-0a8e0167e46c.scope"], stopped);
    }

    /// <summary>验证命名形状不符的 unit 被静默跳过（不猜、不误伤，上游改名即 no-op）。</summary>
    [Fact]
    public void Reap_UnknownShape_SkipsSilently()
    {
        var stopped = new List<string>();

        int reaped = DshSubprocessScopeReaper.Reap(
            ["dsh-subprocess-9884.scope", "unrelated.service", "dsh-subprocess-9884-0a8e0167e46c.scope"],
            isOwnerDead: _ => true,
            stopScope: unit => stopped.Add(unit),
            log: NoLog);

        Assert.Equal(1, reaped);
        Assert.Equal(["dsh-subprocess-9884-0a8e0167e46c.scope"], stopped);
    }

    /// <summary>验证 stop 失败（无权限/恰好退出）留痕继续、不计入成功数、不向上抛。</summary>
    [Fact]
    public void Reap_StopThrows_KeepsGoing()
    {
        var logged = new List<string>();
        int calls = 0;

        int reaped = DshSubprocessScopeReaper.Reap(
            ["dsh-subprocess-1-aaaa.scope", "dsh-subprocess-2-bbbb.scope"],
            isOwnerDead: _ => true,
            stopScope: _ =>
            {
                if (++calls == 1)
                {
                    throw new InvalidOperationException("scope 已消失");
                }
            },
            log: logged.Add);

        Assert.Equal(1, reaped);
        Assert.Contains(logged, line => line.Contains("收割 scope 失败"));
    }

    /// <summary>验证空 unit 列表按无可收割返回 0。</summary>
    [Fact]
    public void Reap_NoUnits_ReturnsZero()
    {
        int reaped = DshSubprocessScopeReaper.Reap(
            [],
            isOwnerDead: _ => true,
            stopScope: _ => throw new InvalidOperationException("不应被调用"),
            log: NoLog);

        Assert.Equal(0, reaped);
    }
}
