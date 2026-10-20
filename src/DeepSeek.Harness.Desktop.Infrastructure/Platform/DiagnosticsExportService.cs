namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 诊断包导出适配器（<see cref="IDiagnosticsExport"/> 的 Infrastructure 实现；ADR native-failure-surface +
/// host-boundary-ports）：组合既有 <see cref="DiagnosticsExporter"/>（home/版本解析与白名单落盘住彼处），
/// 失败留痕后回失败结果；与页面命令面（desktop.diagnostics.export）共用同一导出器，只是入口不同。
/// </summary>
public sealed class DiagnosticsExportService : IDiagnosticsExport
{
    private readonly Action<string>? _log;
    private readonly Func<string?>? _healthSnapshot;
    private readonly string? _home;
    private readonly string? _outputDirectory;
    private readonly string? _appVersion;

    /// <summary>创建适配器。</summary>
    /// <param name="log">日志输出（可选）。</param>
    /// <param name="healthSnapshot">页面健康快照求值（调用期解析；观测未起时可为 null）。</param>
    /// <param name="home">诊断 home（缺省按产品默认在调用期解析）。</param>
    /// <param name="outputDirectory">导出目录（缺省走回落路径）。</param>
    /// <param name="appVersion">写入包内的版本号（缺省取应用当前版本）。</param>
    public DiagnosticsExportService(
        Action<string>? log = null,
        Func<string?>? healthSnapshot = null,
        string? home = null,
        string? outputDirectory = null,
        string? appVersion = null)
    {
        _log = log;
        _healthSnapshot = healthSnapshot;
        _home = home;
        _outputDirectory = outputDirectory;
        _appVersion = appVersion;
    }

    /// <inheritdoc />
    public DiagnosticsExportOutcome Export()
    {
        try
        {
            string home = _home ?? HarnessRuntimeHost.ResolveDshHome();
            string version = _appVersion ?? AppVersion.Current();
            DiagnosticsExportResult result = _outputDirectory is null
                ? DiagnosticsExporter.ExportWithFallback(home, version, _log, _healthSnapshot)
                : DiagnosticsExporter.Export(home, _outputDirectory, version, _healthSnapshot);
            _log?.Invoke($"[host] 诊断包已导出：{result.ZipPath}（{result.Included.Count} 项）");
            return DiagnosticsExportOutcome.Success(result.ZipPath);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[host] 诊断包导出失败：{ex.Message}");
            return DiagnosticsExportOutcome.Failure(ex.Message);
        }
    }
}
