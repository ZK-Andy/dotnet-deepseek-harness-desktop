namespace DeepSeek.Harness.Desktop.Services;

/// <summary>问候语服务：Hello 里程碑里唯一的可测试业务逻辑。</summary>
public static class GreetingService
{
    /// <summary>生成面向 <paramref name="name"/> 的问候文本。</summary>
    /// <param name="name">被问候者；空白时回退为 "World"。</param>
    /// <returns>带上桌面端标识的问候语。</returns>
    public static string Hello(string? name)
    {
        var safe = string.IsNullOrWhiteSpace(name) ? "World" : name.Trim();
        return $"Hello, {safe}! — from DeepSeek.Harness.Desktop (C#)";
    }
}
