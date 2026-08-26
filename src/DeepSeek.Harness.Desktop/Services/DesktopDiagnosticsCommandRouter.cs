using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 宿主命令路由：<c>desktop.diagnostics.export</c>。伴生插件设置页「导出诊断信息」按钮
/// 触发（点击即隐私确认——包内容为白名单日志与状态，不含会话/凭据）；
/// zip 落用户文档目录，返回 <c>{"path":"..."}</c> 供页面展示，失败返回 <c>{"error":"..."}</c>。
/// </summary>
public sealed class DesktopDiagnosticsCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名。</summary>
    public const string CommandName = "desktop.diagnostics.export";

    private readonly string? _home;
    private readonly string? _outputDirectory;
    private readonly string? _appVersion;
    private readonly Action<string>? _log;
    private readonly Func<string?>? _healthSnapshot;

    /// <summary>创建路由；home/输出目录/版本默认运行时解析，日志委托默认不接（测试注入固定值）。
    /// <paramref name="healthSnapshot"/> 导出时刻求值页面健康快照（page-health-monitor 接线）。</summary>
    public DesktopDiagnosticsCommandRouter(
        string? home = null,
        string? outputDirectory = null,
        string? appVersion = null,
        Action<string>? log = null,
        Func<string?>? healthSnapshot = null)
    {
        _home = home;
        _outputDirectory = outputDirectory;
        _appVersion = appVersion;
        _log = log;
        _healthSnapshot = healthSnapshot;
    }

    /// <inheritdoc />
    public bool CanRoute(string command) => string.Equals(command, CommandName, StringComparison.Ordinal);

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!CanRoute(command))
        {
            throw new RynCommandNotFoundException(command);
        }

        try
        {
            var home = _home ?? HarnessRuntimeHost.ResolveDshHome();
            var version = _appVersion ?? Update.AppVersion.Current();
            var result = _outputDirectory is null
                ? DiagnosticsExporter.ExportWithFallback(home, version, _log, _healthSnapshot)
                : DiagnosticsExporter.Export(home, _outputDirectory, version, _healthSnapshot);
            _log?.Invoke($"[host] 诊断包已导出：{result.ZipPath}（{result.Included.Count} 项）");
            return ValueTask.FromResult($"{{\"path\":{Quote(result.ZipPath)}}}");
        }
        catch (Exception ex)
        {
            // 导出失败不抛 IPC 异常：页面按 error 展示原因，与更新路由同款帧形态
            _log?.Invoke($"[host] 诊断包导出失败：{ex.Message}");
            return ValueTask.FromResult($"{{\"error\":\"{JsonEncodedText.Encode(ex.Message)}\"}}");
        }
    }

    private static string Quote(string value) => $"\"{JsonEncodedText.Encode(value)}\"";
}
