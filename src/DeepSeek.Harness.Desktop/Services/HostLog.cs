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

    /// <summary>写一条诊断日志（时间戳前缀；目录不存在则创建；超限先滚动）。</summary>
    public static void Write(string msg) => Write(HarnessRuntimeHost.ResolveDshHome(), msg);

    /// <summary>同 <see cref="Write(string)"/>，home 由调用方给定（CLI/测试注入用）。</summary>
    public static void Write(string home, string msg)
    {
        Console.WriteLine(msg);
        try
        {
            var dir = Path.Combine(home, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "host.log");
            RotateIfNeeded(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
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

        var old = logPath + ".old";
        File.Delete(old);
        File.Move(logPath, old);
    }
}
