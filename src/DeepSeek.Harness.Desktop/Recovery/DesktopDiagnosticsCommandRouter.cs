using System.Text.Json;
using DeepSeek.Harness.Desktop.Core.Ipc;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>
/// 宿主命令路由：<c>desktop.diagnostics.export</c>。伴生插件设置页「导出诊断信息」按钮
/// 触发（点击即隐私确认——包内容为白名单日志与状态，不含会话/凭据）；
/// zip 落用户文档目录，返回 <c>{"path":"..."}</c> 供页面展示，失败返回 <c>{"error":"..."}</c>。
/// </summary>
/// <remarks>
/// 导出动作经 <see cref="IDiagnosticsExport"/> 端口（ADR host-boundary-ports）：与失败面自动导出
/// 共用同一导出器，壳侧不再直调基础设施静态面（原先静态/端口双路径）。
/// </remarks>
public sealed class DesktopDiagnosticsCommandRouter : ICommandRouter
{
    /// <summary>本路由响应的命令名。</summary>
    public const string CommandName = HostCommandIds.DiagnosticsExport;

    private readonly IDiagnosticsExport _export;
    private readonly Action<string>? _log;

    /// <summary>创建路由。</summary>
    /// <param name="export">诊断包导出端口（Infrastructure 实现；失败面与页面命令面共用同一实现）。</param>
    /// <param name="log">日志委托（可选；生产传 HostLog.Write，测试注入收集器）。</param>
    public DesktopDiagnosticsCommandRouter(IDiagnosticsExport export, Action<string>? log = null)
    {
        _export = export;
        _log = log;
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

        // 导出失败不抛 IPC 异常：页面按 error 展示原因，与更新路由同款帧形态。
        // 端口契约要求实现不抛（失败进结果对象），此处仍兜一层——实现违约不至于变成 IPC 异常。
        DiagnosticsExportOutcome outcome;
        try
        {
            outcome = _export.Export();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[host] 诊断包导出失败：{ex.Message}");
            return ValueTask.FromResult(AppJsonContext.Error(ex.Message));
        }

        if (!outcome.Ok)
        {
            // 失败原因已由导出实现记日志（同一出口，不重复记一条）
            return ValueTask.FromResult(AppJsonContext.Error(outcome.Error ?? "诊断包导出失败"));
        }

        return ValueTask.FromResult(
            JsonSerializer.Serialize(new PathFrame(outcome.Path), AppJsonContext.Default.PathFrame));
    }

    /// <summary>导出成功帧 <c>{"path":"..."}</c>；internal 供 <see cref="AppJsonContext"/> 源生成注册。</summary>
    /// <param name="Path">zip 落盘绝对路径（页面展示「已保存至」）。</param>
    internal sealed record PathFrame(string Path);
}
