using System.IO.Compression;
using System.Text;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>诊断包导出：白名单收录、敏感排除、zip 内容与 state 快照。</summary>
public class DiagnosticsExporterTests
{
    [Fact]
    public void Export_WhitelistedEntriesOnly_SensitiveExcluded()
    {
        var home = NewDir();
        var outDir = NewDir();
        try
        {
            // home 内同时布置「应收录」与「必须排除」的文件
            WriteFile(home, "logs/host.log", "host log line");
            WriteFile(home, ".dsh-web-port", "4242");
            WriteFile(home, ".credentials.yaml", "SECRET-CREDENTIALS");
            WriteFile(home, "sessions/session.json", "SECRET-SESSION");
            WriteFile(home, "profiles/desktop/package.json", "{}");
            WriteFile(home, "storages/workspace.json", "{}");

            var result = DiagnosticsExporter.Export(home, outDir, appVersion: "9.9.9-test");

            Assert.True(File.Exists(result.ZipPath));
            using var zip = ZipFile.OpenRead(result.ZipPath);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();

            Assert.Contains("logs/host.log", names);
            Assert.Contains("state/web-port.txt", names);
            Assert.Contains("state/state.txt", names);

            var stateReader = new StreamReader(
                zip.GetEntry("state/state.txt")!.Open(), Encoding.UTF8);
            var state = stateReader.ReadToEnd();
            Assert.Contains("9.9.9-test", state);
            Assert.Contains(home, state);

            // 敏感面绝不进包（内容级断言，防止仅路径巧合）
            Assert.DoesNotContain(".credentials.yaml", names);
            Assert.DoesNotContain("sessions/session.json", names);
            Assert.DoesNotContain("profiles/desktop/package.json", names);
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain("SECRET-", reader.ReadToEnd());
            }

            // 可选缺失文件（.old / marker）不出现也不报错
            Assert.DoesNotContain("logs/host.log.old", names);
            Assert.Equal(3, result.Included.Count);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
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
        var home = NewDir();
        var outDir = NewDir();
        try
        {
            var router = new DesktopDiagnosticsCommandRouter(
                home: home, outputDirectory: outDir, appVersion: "1.2.3-test", log: _ => { });
            var frame = await router.RouteAsync(
                "desktop.diagnostics.export",
                ReadOnlyMemory<byte>.Empty,
                services: null!,
                CancellationToken.None);

            Assert.Contains("\"path\":", frame);
            var path = System.Text.Json.JsonDocument.Parse(frame).RootElement.GetProperty("path").GetString();
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
        var fileAsDir = Path.Combine(NewDir(), "not-a-dir");
        File.WriteAllText(fileAsDir, "x");
        try
        {
            var logs = new List<string>();
            var router = new DesktopDiagnosticsCommandRouter(
                home: fileAsDir,
                outputDirectory: fileAsDir,
                appVersion: "0.0.0-test",
                log: msg => logs.Add(msg));

            var frame = await router.RouteAsync(
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
        var dir = Path.Combine(Path.GetTempPath(), "dsh-diag-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
