namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// host 诊断日志统一出口：stdout 与 <c>&lt;DSH_HOME&gt;/logs/host.log</c> 双写。
/// 桌面启动形态下 stdout 不可见（v0.2.1 实证教训），落盘是唯一可靠通道；
/// 写失败仅回显控制台，绝不拖垮启动链路。
/// </summary>
internal static class HostLog
{
    /// <summary>写一条诊断日志（时间戳前缀；目录不存在则创建）。</summary>
    public static void Write(string msg)
    {
        Console.WriteLine(msg);
        try
        {
            var path = Path.Combine(HarnessRuntimeHost.ResolveDshHome(), "logs", "host.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch (Exception logEx)
        {
            Console.WriteLine($"[host] 日志落盘失败：{logEx.Message}");
        }
    }
}
