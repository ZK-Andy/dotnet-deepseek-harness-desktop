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
        (".dsh-web-port", "state/web-port.txt"),
        ("logs/run-marker.json", "state/run-marker.json"),
    };

    /// <summary>生成 state.txt 内容（纯函数可单测）：版本/平台/home/时间戳等非敏感快照。</summary>
    public static string BuildStateText(string appVersion, string home)
    {
        return $"""
            dsh-desktop diagnostics
            =======================
            exportedAt : {DateTimeOffset.Now.ToString("o")}
            appVersion : {appVersion}
            os         : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}
            arch       : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}
            home       : {home}
            """;
    }

    /// <summary>导出诊断 zip（带回退）：优先用户文档目录；不可写（只读/无目录权限）时
    /// 回退 <c>&lt;home&gt;/diagnostics/</code> 并留痕——取证功能不因环境怪异而缺席。</summary>
    public static DiagnosticsExportResult ExportWithFallback(string home, string appVersion)
    {
        try
        {
            return Export(home, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), appVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var fallback = Path.Combine(home, "diagnostics");
            var result = Export(home, fallback, appVersion);
            Console.WriteLine($"[host] 文档目录不可写（{ex.Message}），诊断包已回退到 {fallback}");
            return result;
        }
    }

    /// <summary>导出诊断 zip。</summary>
    /// <param name="home">共享 DSH_HOME。</param>
    /// <param name="outputDirectory">zip 输出目录（默认调用方传用户文档目录）。</param>
    /// <param name="appVersion">写入 state.txt 的壳版本。</param>
    /// <returns>zip 绝对路径与实际收录条目清单。</returns>
    public static DiagnosticsExportResult Export(string home, string outputDirectory, string appVersion)
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
                writer.Write(BuildStateText(appVersion, home));
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
