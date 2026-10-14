namespace DeepSeek.Harness.Desktop.Core.Recovery;

/// <summary>
/// 诊断包导出端口（R3：外部副作用经接口；ADR native-failure-surface）：失败面自动导出用——原生确认模态只有
/// 两个按钮，求助路径改以「自动导出 + 路径进正文」保留；实现失败留痕并回空串。
/// </summary>
public interface IDiagnosticsExport
{
    /// <summary>导出诊断包并返回落盘绝对路径；失败返回空串（原因由实现记日志）。</summary>
    /// <returns>zip 绝对路径；失败空串。</returns>
    string Export();
}
