namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>HostLog 出口脱敏集成（ADR diag-masking-and-recovery-page）：落盘内容不得含凭据形状。</summary>
public class HostLogMaskingTests
{
    /// <summary>验证落盘前对 sk- 密钥、token 查询参数与 Cookie 值做整值遮罩，日志只含 *** 形态且时间戳前缀保留。</summary>
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
