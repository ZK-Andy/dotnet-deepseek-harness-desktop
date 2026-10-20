namespace DeepSeek.Harness.Desktop.Core.Recovery;

/// <summary>
/// 诊断包导出结果（ADR host-boundary-ports）：一次导出的落点或失败原因。
/// 失败面只需路径（失败即空串），页面命令面要失败原因（回给用户看），故结果带原因而非只带空串。
/// </summary>
/// <param name="Ok">导出是否成功。</param>
/// <param name="Path">zip 落盘绝对路径；失败为空串。</param>
/// <param name="Error">失败原因；成功为 null。</param>
public sealed record DiagnosticsExportOutcome(bool Ok, string Path, string? Error)
{
    /// <summary>成功结果。</summary>
    /// <param name="path">zip 落盘绝对路径。</param>
    /// <returns>成功结果。</returns>
    public static DiagnosticsExportOutcome Success(string path) => new(true, path, null);

    /// <summary>失败结果。</summary>
    /// <param name="error">失败原因。</param>
    /// <returns>失败结果（路径为空串）。</returns>
    public static DiagnosticsExportOutcome Failure(string error) => new(false, string.Empty, error);
}
