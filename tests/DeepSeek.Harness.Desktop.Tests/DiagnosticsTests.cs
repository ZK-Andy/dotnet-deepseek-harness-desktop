using System.IO.Compression;
using System.Text;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>诊断包导出：白名单收录、敏感排除、zip 内容与 state 快照。</summary>
public class DiagnosticsExporterTests
{
    [Fact]
    public void Export_WhitelistedEntriesOnly_SensitiveExcluded()
    {
        string home = NewDir();
        string outDir = NewDir();
        try
        {
            // home 内同时布置「应收录」与「必须排除」的文件
            WriteFile(home, "logs/host.log", "host log line");
            WriteFile(home, "profiles/desktop/.dsh-web-port", "4242");
            WriteFile(home, ".dsh-web-port", "1111");
            WriteFile(home, ".credentials.yaml", "SECRET-CREDENTIALS");
            WriteFile(home, "sessions/session.json", "SECRET-SESSION");
            WriteFile(home, "profiles/desktop/package.json", "{}");
            WriteFile(home, "storages/workspace.json", "{}");

            DiagnosticsExportResult result = DiagnosticsExporter.Export(home, outDir, appVersion: "9.9.9-test");

            Assert.True(File.Exists(result.ZipPath));
            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();

            Assert.Contains("logs/host.log", names);
            Assert.Contains("state/web-port.txt", names);
            Assert.Contains("state/state.txt", names);

            var stateReader = new StreamReader(
                zip.GetEntry("state/state.txt")!.Open(), Encoding.UTF8);
            string state = stateReader.ReadToEnd();
            Assert.Contains("9.9.9-test", state);
            Assert.Contains(home, state);

            // 敏感面绝不进包（内容级断言，防止仅路径巧合）
            Assert.DoesNotContain(".credentials.yaml", names);
            Assert.DoesNotContain("sessions/session.json", names);
            Assert.DoesNotContain("profiles/desktop/package.json", names);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain("SECRET-", reader.ReadToEnd());
            }

            // 可选缺失文件（.old / marker）不出现也不报错
            Assert.DoesNotContain("logs/host.log.old", names);
            Assert.Contains("state/web-port-legacy.txt", names);
            Assert.Equal(4, result.Included.Count);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    /// <summary>回退链回归（v0.3.0 实机验收批次）：文档目录解析为空 / 不可写时，
    /// zip 必须落到 &lt;home&gt;/diagnostics 且原因留痕 log——取证功能不缺席也不静默。</summary>
    public class ExportFallbackTests
    {
        [Fact]
        public void EmptyDocuments_FallsBackToHomeDiagnostics_WithLog()
        {
            string home = NewDir();
            var logs = new List<string>();
            try
            {
                WriteFile(home, "logs/host.log", "line");

                DiagnosticsExportResult result = DiagnosticsExporter.ExportWithFallback(
                    home, appVersion: "0.3.1-test", documentsDirectory: "", log: logs.Add);

                Assert.StartsWith(Path.Combine(home, "diagnostics"), result.ZipPath);
                Assert.True(File.Exists(result.ZipPath));
                Assert.Contains(logs, m => m.Contains("文档目录解析为空") && m.Contains("diagnostics"));
            }
            finally
            {
                Directory.Delete(home, recursive: true);
            }
        }

        [Fact]
        public void UnwritableDocuments_FallsBackToHomeDiagnostics_WithReason()
        {
            string home = NewDir();
            // 文档目录指向一个常规文件：CreateDirectory 必败，异常落在回退过滤内
            string fileAsDir = Path.Combine(home, "documents-file");
            File.WriteAllText(fileAsDir, "x");
            var logs = new List<string>();
            try
            {
                DiagnosticsExportResult result = DiagnosticsExporter.ExportWithFallback(
                    home, appVersion: "0.3.1-test", documentsDirectory: fileAsDir, log: logs.Add);

                Assert.StartsWith(Path.Combine(home, "diagnostics"), result.ZipPath);
                Assert.True(File.Exists(result.ZipPath));
                Assert.Contains(logs, m => m.Contains("文档目录不可写") && m.Contains(fileAsDir));
            }
            finally
            {
                Directory.Delete(home, recursive: true);
            }
        }

        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "dsh-diag-fallback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteFile(string root, string relative, string content)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

/// <summary>诊断导出命令路由：帧形态与失败路径。</summary>
public class DesktopDiagnosticsCommandRouterTests
{
    [Fact]
    public void CanRoute_ExactCommandOnly()
    {
        var router = new DesktopDiagnosticsCommandRouter();
        Assert.True(router.CanRoute("desktop.diagnostics.export"));
        Assert.False(router.CanRoute("desktop.diagnostics.export.extra"));
        Assert.False(router.CanRoute("app.openExternal"));
    }

    [Fact]
    public async Task RouteAsync_Success_ReturnsPathFrame_AndWritesZip()
    {
        string home = NewDir();
        string outDir = NewDir();
        try
        {
            var router = new DesktopDiagnosticsCommandRouter(
                home: home, outputDirectory: outDir, appVersion: "1.2.3-test", log: _ => { });
            string frame = await router.RouteAsync(
                "desktop.diagnostics.export",
                ReadOnlyMemory<byte>.Empty,
                services: null!,
                CancellationToken.None);

            Assert.Contains("\"path\":", frame);
            string? path = System.Text.Json.JsonDocument.Parse(frame).RootElement.GetProperty("path").GetString();
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task RouteAsync_Failure_ReturnsErrorFrame_NotThrow()
    {
        // 输出目录指向一个常规文件：Directory.CreateDirectory 必败 → error 帧
        string fileAsDir = Path.Combine(NewDir(), "not-a-dir");
        File.WriteAllText(fileAsDir, "x");
        try
        {
            var logs = new List<string>();
            var router = new DesktopDiagnosticsCommandRouter(
                home: fileAsDir,
                outputDirectory: fileAsDir,
                appVersion: "0.0.0-test",
                log: msg => logs.Add(msg));

            string frame = await router.RouteAsync(
                "desktop.diagnostics.export",
                ReadOnlyMemory<byte>.Empty,
                services: null!,
                CancellationToken.None);

            Assert.Contains("\"error\":", frame);
            Assert.NotEmpty(logs);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(fileAsDir)!, recursive: true);
        }
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-diag-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
