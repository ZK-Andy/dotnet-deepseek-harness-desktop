namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>CLI 诊断导出入口的路由契约（ADR restructure-compose-root-final-moves）：开关字面量
/// 与用户可见契约（docs/user-guide 双语的 <c>--export-diagnostics</c>）逐字节一致；
/// 非诊断调用零副作用返回 null（Run 的执行面 = 真实导出，由冒烟/实机覆盖，不在此桩）。</summary>
public class DiagnosticsCliTests
{
    /// <summary>开关字面量即用户契约本体：与 docs/user-guide.md / user-guide.en.md 的 CLI 用法一致。</summary>
    [Fact]
    public void ArgExportDiagnostics_MatchesUserContract()
    {
        Assert.Equal("--export-diagnostics", DiagnosticsCli.ArgExportDiagnostics);
    }

    /// <summary>判定纯函数：开关在场即请求（与顺序/多余参数无关），不在场即否。</summary>
    [Fact]
    public void IsRequested_MatchesFlagPositionIndependently()
    {
        Assert.True(DiagnosticsCli.IsRequested(["--export-diagnostics"]));
        Assert.True(DiagnosticsCli.IsRequested(["--other", "--export-diagnostics", "x"]));
        Assert.False(DiagnosticsCli.IsRequested([]));
        Assert.False(DiagnosticsCli.IsRequested(["--export-diagnostics-suffix"]));
        Assert.False(DiagnosticsCli.IsRequested(["--export"]));
    }

    /// <summary>非诊断调用返回 null（入口照常走壳启动），不触发任何导出副作用。</summary>
    [Fact]
    public void TryRun_NonDiagnosticArgs_ReturnsNull()
    {
        Assert.Null(DiagnosticsCli.TryRun([]));
        Assert.Null(DiagnosticsCli.TryRun(["--some-future-flag"]));
    }
}
