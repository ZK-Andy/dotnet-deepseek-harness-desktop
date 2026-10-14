namespace DeepSeek.Harness.Desktop.Core.Recovery;

/// <summary>原生对话框可用性判定（纯函数可单测；ADR native-failure-surface）：Windows/macOS 恒可用；Linux 需
/// zenity 或 kdialog 之一在 PATH 上且带执行位（插件探针口径），无则回落页内恢复面。</summary>
public static class DialogToolAvailability
{
    private static readonly string[] s_linuxTools = ["zenity", "kdialog"];

    /// <summary>原生对话框是否可用。</summary>
    /// <param name="isWindows">当前平台是否 Windows。</param>
    /// <param name="isMacOs">当前平台是否 macOS。</param>
    /// <param name="isLinux">当前平台是否 Linux。</param>
    /// <param name="pathVariable">PATH 变量值（可空）。</param>
    /// <param name="isExecutable">路径可执行性判定（生产传「存在 + Unix 执行位」，测试注入）。</param>
    /// <returns>可用 true；需回落 false。</returns>
    public static bool IsAvailable(
        bool isWindows,
        bool isMacOs,
        bool isLinux,
        string? pathVariable,
        Func<string, bool> isExecutable)
    {
        if (isWindows || isMacOs)
        {
            return true;
        }

        return isLinux && FindLinuxTool(pathVariable, isExecutable) is not null;
    }

    /// <summary>在 PATH 上按候选序找对话框工具（tool-major：与 Ryn 插件的 <c>which</c> 顺序一致）。
    /// 只服务本类判定；测试经 <see cref="IsAvailable"/> 的探针谓词核对候选序。</summary>
    private static string? FindLinuxTool(string? pathVariable, Func<string, bool> isExecutable)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        string[] dirs = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string tool in s_linuxTools)
        {
            foreach (string dir in dirs)
            {
                if (isExecutable(Path.Combine(dir, tool)))
                {
                    return tool;
                }
            }
        }

        return null;
    }
}
