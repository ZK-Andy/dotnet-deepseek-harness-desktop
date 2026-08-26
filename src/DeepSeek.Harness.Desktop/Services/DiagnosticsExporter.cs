using System.IO.Compression;
using System.Text;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 诊断包导出（ADR shell-observability-diagnostics）：把日志与关键运行状态打成单个 zip，
/// 落到用户文档目录（文件管理器稳定可见）。内容走**白名单**——只收 host.log（含 .old）、
/// 端口状态文件与生成的 state.txt；credentials/sessions/profiles/storages/updates/pnpm
/// 等一律不进包。未来 home 内新增敏感路径时必须回查此清单。
/// </summary>
public static class DiagnosticsExporter
{
    /// <summary>白名单：home 内相对路径 → zip 内条目名。</summary>
    internal static readonly (string Relative, string Entry)[] IncludedFiles =
    {
        ("logs/host.log", "logs/host.log"),
        ("logs/host.log.old", "logs/host.log.old"),
        ("profiles/desktop/.dsh-web-port", "state/web-port.txt"),
        (".dsh-web-port", "state/web-port-legacy.txt"),
        ("logs/run-marker.json", "state/run-marker.json"),
    };

    /// <summary>生成 state.txt 内容（纯函数可单测）：版本/平台/home/时间戳等非敏感快照；
    /// 健康行来自页面健康观测（ADR page-health-monitor），未启用或尚无有效探针时记 n/a。</summary>
    public static string BuildStateText(string appVersion, string home, string? health = null)
    {
        return $"""
            dsh-desktop diagnostics
            =======================
            exportedAt : {DateTimeOffset.Now.ToString("o")}
            appVersion : {appVersion}
            os         : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}
            arch       : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}
            home       : {home}
            health     : {health ?? "n/a（未启用或尚无有效探针）"}
            """;
    }

    /// <summary>导出诊断 zip（带回退）：优先用户文档目录；未解析或不可写时
    /// 回退 <c>&lt;home&gt;/diagnostics/</c> 并留痕（经 <paramref name="log"/> 进 host.log，
    /// Console 在打包产品不可见）——取证功能不因环境怪异而缺席。
    /// <paramref name="healthSnapshot"/> 导出时刻惰性求值，收录页面健康观测最新快照。</summary>
    public static DiagnosticsExportResult ExportWithFallback(string home, string appVersion, Action<string>? log = null, Func<string?>? healthSnapshot = null)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return ExportWithFallback(home, appVersion, documents, log, healthSnapshot);
    }

    /// <summary>可注入文档目录的核心实现（internal，供回归测试模拟解析为空/不可写环境）。</summary>
    internal static DiagnosticsExportResult ExportWithFallback(
        string home, string appVersion, string documentsDirectory, Action<string>? log, Func<string?>? healthSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(documentsDirectory))
        {
            // 文档目录解析为空（如 xdg-user-dirs 未初始化）——空路径会让 Path.Combine 产出相对路径，
            // 后续异常类型还不落在回退过滤内，必须显式守卫直走回退
            return ExportToFallback(home, appVersion, "文档目录解析为空", log, healthSnapshot);
        }

        try
        {
            return Export(home, documentsDirectory, appVersion, healthSnapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExportToFallback(home, appVersion, $"文档目录不可写（{ex.Message}）", log, healthSnapshot);
        }
    }

    /// <summary>回退导出：<c>&lt;home&gt;/diagnostics/</c>，原因留痕 host.log。</summary>
    private static DiagnosticsExportResult ExportToFallback(string home, string appVersion, string reason, Action<string>? log, Func<string?>? healthSnapshot = null)
    {
        var fallback = Path.Combine(home, "diagnostics");
        var result = Export(home, fallback, appVersion, healthSnapshot);
        log?.Invoke($"[host] {reason}，诊断包已回退到 {fallback}");
        return result;
    }

    /// <summary>导出诊断 zip。</summary>
    /// <param name="home">共享 DSH_HOME。</param>
    /// <param name="outputDirectory">zip 输出目录（默认调用方传用户文档目录）。</param>
    /// <param name="appVersion">写入 state.txt 的壳版本。</param>
    /// <param name="healthSnapshot">导出时刻的页面健康快照求值委托（可空）。</param>
    /// <returns>zip 绝对路径与实际收录条目清单。</returns>
    public static DiagnosticsExportResult Export(string home, string outputDirectory, string appVersion, Func<string?>? healthSnapshot = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var zipPath = Path.Combine(
            outputDirectory,
            $"dsh-desktop-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        var included = new List<string>();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (relative, entry) in IncludedFiles)
            {
                var source = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    continue;
                }

                zip.CreateEntryFromFile(source, entry);
                included.Add(entry);
            }

            var stateEntry = zip.CreateEntry("state/state.txt");
            using (var writer = new StreamWriter(stateEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(BuildStateText(appVersion, home, healthSnapshot?.Invoke()));
            }

            included.Add("state/state.txt");
        }

        return new DiagnosticsExportResult(zipPath, included);
    }
}

/// <summary><see cref="DiagnosticsExporter.Export"/> 的结果。</summary>
/// <param name="ZipPath">zip 绝对路径。</param>
/// <param name="Included">实际收录的条目名（缺失的可选文件不出现）。</param>
public sealed record DiagnosticsExportResult(string ZipPath, IReadOnlyList<string> Included);
