namespace DeepSeek.Harness.Desktop.Core.Recovery;

/// <summary>
/// 诊断包导出端口（R3：外部副作用经接口；ADR native-failure-surface + host-boundary-ports）：
/// 失败面自动导出与页面命令面（desktop.diagnostics.export）共用同一导出器——只是一个要路径、
/// 一个要失败原因，故返回结果而非裸字符串。
/// </summary>
public interface IDiagnosticsExport
{
    /// <summary>导出诊断包。<b>实现不得抛异常</b>——失败以结果对象表达（原因进 <see cref="DiagnosticsExportOutcome.Error"/>），
    /// 调用方据此映射帧，不把异常透到 IPC 面。</summary>
    /// <returns>导出结果（成功带落盘路径，失败带原因）。</returns>
    DiagnosticsExportOutcome Export();
}
