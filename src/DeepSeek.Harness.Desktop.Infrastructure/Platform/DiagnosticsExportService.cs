using DeepSeek.Harness.Desktop.Core.Recovery;

namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 诊断包导出适配器（<see cref="IDiagnosticsExport"/> 的 Infrastructure 实现；ADR native-failure-surface）：
/// 组合既有 <see cref="DiagnosticsExporter"/>（home/版本解析与白名单落盘住彼处），失败留痕后回空串；
/// 与页面命令面（desktop.diagnostics.export）共用同一导出器，只是入口不同。
/// </summary>
public sealed class DiagnosticsExportService : IDiagnosticsExport
{
    private readonly Action<string>? _log;
    private readonly Func<string?>? _healthSnapshot;

    /// <summary>创建适配器。</summary>
    /// <param name="log">日志输出（可选）。</param>
    /// <param name="healthSnapshot">页面健康快照求值（调用期解析；观测未起时可为 null）。</param>
    public DiagnosticsExportService(Action<string>? log = null, Func<string?>? healthSnapshot = null)
    {
        _log = log;
        _healthSnapshot = healthSnapshot;
    }

    /// <inheritdoc />
    public string Export()
    {
        try
        {
            DiagnosticsExportResult result = DiagnosticsExporter.ExportWithFallback(
                HarnessRuntimeHost.ResolveDshHome(), AppVersion.Current(), _log, _healthSnapshot);
            _log?.Invoke($"[host] 诊断包已导出：{result.ZipPath}（{result.Included.Count} 项）");
            return result.ZipPath;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[host] 诊断包导出失败：{ex.Message}");
            return string.Empty;
        }
    }
}
