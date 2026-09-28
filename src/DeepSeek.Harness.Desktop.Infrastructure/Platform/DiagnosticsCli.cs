namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// CLI 专用诊断导出入口（原组合根 <c>Program.ExportDiagnostics</c> 随细搬下沉，ADR
/// restructure-compose-root-final-moves）：无 UI 兜底取证路径——不 spawn dsh、不开窗、不做 dev 隔离，
/// 覆盖「闪退进不了界面」的场景（ADR shell-observability-diagnostics）。开关字面量与实现同文件单源；
/// 用户可见契约（<c>--export-diagnostics</c>）不变（docs/user-guide 双语记载）。
/// </summary>
public static class DiagnosticsCli
{
    /// <summary>诊断导出的命令行开关（用户契约：与 docs/user-guide 双语的 CLI 用法逐字节一致，改名须两侧同审）。</summary>
    public const string ArgExportDiagnostics = "--export-diagnostics";

    /// <summary>判定 <paramref name="args"/> 是否为诊断导出调用（纯函数，可单测）。</summary>
    public static bool IsRequested(string[] args) => Array.IndexOf(args, ArgExportDiagnostics) >= 0;

    /// <summary>请求即执行导出并返回进程退出码；非诊断调用返回 null（调用方照常走壳启动）。</summary>
    /// <param name="args">进程命令行参数。</param>
    /// <returns>退出码（0 成功；失败非零 fail loud，脚本可判定），非诊断调用为 null。</returns>
    public static int? TryRun(string[] args) => IsRequested(args) ? Run() : null;

    /// <summary>执行导出：CLI 形态下 stdout 可见；经 HostLog 双写让桌面形态的同一动作也落 host.log；
    /// 失败以非零退出码 fail loud（不抛）。</summary>
    /// <returns>0 成功；1 导出失败（原因已写 stderr）。</returns>
    public static int Run()
    {
        try
        {
            DiagnosticsExportResult result = DiagnosticsExporter.ExportWithFallback(
                HarnessRuntimeHost.ResolveDshHome(),
                AppVersion.Current());
            HostLog.Write($"[host] 诊断包已导出：{result.ZipPath}");
            return 0;
        }
        catch (Exception ex)
        {
            // D003 命名所吞：导出失败经 stderr loud（CLI 面无 host.log 消费方），退出码即失败信号
            Console.Error.WriteLine($"[host] 诊断包导出失败：{ex.Message}");
            return 1;
        }
    }
}
