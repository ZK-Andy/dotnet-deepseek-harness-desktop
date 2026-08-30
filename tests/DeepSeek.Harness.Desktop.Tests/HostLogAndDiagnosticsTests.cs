using System.IO.Compression;
using System.Text;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>HostLog 出口脱敏集成（ADR diag-masking-and-recovery-page）：落盘内容不得含凭据形状。</summary>
public class HostLogMaskingTests
{
    [Fact]
    public void Write_MasksSecretShapes_BeforePersisting()
    {
        string home = NewDir();
        try
        {
            // sk- 形态刻意置于 Cookie 之前：头行整值遮罩会吞掉行内自冒号起的剩余内容（设计行为）
            HostLog.Write(home, "key=sk-Abc12345Defghi | POST /auth?token=abcdef1234567890 Cookie: sid=zzz");

            string logged = File.ReadAllText(Path.Combine(home, "logs", "host.log"));
            Assert.DoesNotContain("abcdef1234567890", logged);
            Assert.DoesNotContain("sid=zzz", logged);
            Assert.Contains("token=***", logged);
            Assert.Contains("sk-***", logged);
            Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] key=sk-\*\*\*", logged); // 时间戳前缀仍在
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ddc-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>state.txt 健康行（ADR page-health-monitor）：快照收录与 n/a 缺省。</summary>
public class DiagnosticsHealthSnapshotTests
{
    [Fact]
    public void BuildStateText_WithHealth_IncludesSnapshot()
    {
        string state = DiagnosticsExporter.BuildStateText("1.2.3-test", "/home/x", health: "alive @ 2026-08-26T01:00:00+08:00 (probes 42)");

        Assert.Contains("health     : alive @ 2026-08-26T01:00:00+08:00 (probes 42)", state);
    }

    [Fact]
    public void BuildStateText_WithoutHealth_RecordsNa()
    {
        string state = DiagnosticsExporter.BuildStateText("1.2.3-test", "/home/x");

        Assert.Contains("health     : n/a（未启用或尚无有效探针）", state);
    }

    [Fact]
    public void Export_EvaluatesSnapshotAtExportTime()
    {
        string home = Path.Combine(Path.GetTempPath(), "ddc-hs-" + Guid.NewGuid().ToString("N"));
        string outDir = Path.Combine(home, "out");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(outDir);
        try
        {
            string? snapshot = null;
            DiagnosticsExportResult result = DiagnosticsExporter.Export(
                home, outDir, appVersion: "9.9.9-test", healthSnapshot: () => snapshot ??= $"alive @ T-{Guid.NewGuid():N}"[..40]);

            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            string state = new StreamReader(zip.GetEntry("state/state.txt")!.Open(), Encoding.UTF8).ReadToEnd();
            Assert.Matches("health     : alive @ T-", state);
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }
}
