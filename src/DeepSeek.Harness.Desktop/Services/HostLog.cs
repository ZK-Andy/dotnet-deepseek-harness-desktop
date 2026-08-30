namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// host 诊断日志统一出口：stdout 与 <c>&lt;DSH_HOME&gt;/logs/host.log</c> 双写。
/// 桌面启动形态下 stdout 不可见（v0.2.1 实证教训），落盘是唯一可靠通道；
/// 写失败仅回显控制台，绝不拖垮启动链路。超限滚动保留一代（host.log.old），
/// 只保一代是批次二的有意取舍（ADR shell-observability-diagnostics）。
/// </summary>
internal static class HostLog
{
    /// <summary>单文件滚动阈值；超过即把当前日志滚为 .old（覆盖上一代）。</summary>
    internal const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>进程内写锁：supervisor/IPC/后台安装/更新任务多线程并发追加，无锁时 File.AppendAllText
    /// 撞锁的 IOException 兜底只进 stdout（桌面形态不可见）——事故高发期恰好丢日志。</summary>
    private static readonly object s_fileGate = new();

    /// <summary>写一条诊断日志（时间戳前缀；目录不存在则创建；超限先滚动）。</summary>
    public static void Write(string msg) => Write(HarnessRuntimeHost.ResolveDshHome(), msg);

    /// <summary>同 <see cref="Write(string)"/>，home 由调用方给定（CLI/测试注入用）。
    /// 内容先经 <see cref="SecretMasker.Mask"/> 脱敏：本方法是 stdout 与落盘的唯一出口，
    /// 诊断 zip 外发的 host.log 原文由此保证不含凭据形状内容（ADR diag-masking-and-recovery-page）。</summary>
    public static void Write(string home, string msg)
    {
        // Mask 对非空入参恒非空（空串原样透传），此处结果必为具体文本
        msg = SecretMasker.Mask(msg)!;
        Console.WriteLine(msg);
        try
        {
            string dir = Path.Combine(home, "logs");
            string path = Path.Combine(dir, "host.log");
            lock (s_fileGate)
            {
                Directory.CreateDirectory(dir);
                RotateIfNeeded(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
        }
        catch (Exception logEx)
        {
            Console.WriteLine($"[host] 日志落盘失败：{logEx.Message}");
        }
    }

    /// <summary>超限滚动：当前日志改名 .old（覆盖旧一代）；其余情况零动作。纯 IO 判定可直测。</summary>
    internal static void RotateIfNeeded(string logPath)
    {
        if (!File.Exists(logPath) || new FileInfo(logPath).Length <= MaxBytes)
        {
            return;
        }

        string old = logPath + ".old";
        File.Delete(old);
        File.Move(logPath, old);
    }
}
