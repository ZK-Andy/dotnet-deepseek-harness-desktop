using DeepSeek.Harness.Desktop.Core.Recovery;

namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>
/// 假诊断导出端口（<see cref="IDiagnosticsExport"/> 替身）：返回预置结果——
/// 成功给落盘路径，失败给原因；不落真实 zip。
/// </summary>
internal sealed class FakeDiagnosticsExport : IDiagnosticsExport
{
    private readonly DiagnosticsExportOutcome _outcome;

    /// <summary>创建替身。</summary>
    /// <param name="outcome">预置结果（缺省为成功 + 假路径）。</param>
    public FakeDiagnosticsExport(DiagnosticsExportOutcome? outcome = null) =>
        _outcome = outcome ?? DiagnosticsExportOutcome.Success("/tmp/fake-diagnostics.zip");

    /// <inheritdoc />
    public DiagnosticsExportOutcome Export() => _outcome;
}
