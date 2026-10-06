namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// appsettings.json 读文件的 fail-safe 半边（Infrastructure 侧——文件 IO 不进 Core，D005 边界纪律；
/// 解析半边的单源在 Core 侧 <c>ConfigSectionJson</c>，ADR config-load-fail-safe-and-symlink-privilege-fallback）。
/// </summary>
internal static class ConfigSectionFile
{
    /// <summary>读 <paramref name="baseDirectory"/> 旁的 appsettings.json 全文交 <paramref name="parse"/>；
    /// 文件缺失或读失败/解析失败（损坏 JSON/不可读——<see cref="System.Text.Json.JsonException"/>/
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>）一律回退
    /// <paramref name="fallback"/>，不阻塞启动；失败原因经 <paramref name="log"/> 留痕（null = 静默）。</summary>
    /// <typeparam name="T">选项记录类型。</typeparam>
    /// <param name="baseDirectory">appsettings.json 所在目录（宿主为 AppContext.BaseDirectory）。</param>
    /// <param name="sectionName">节名（仅用于留痕行文定位节）。</param>
    /// <param name="parse">解析半边（各选项类型的 <c>Parse</c> 纯函数）。</param>
    /// <param name="fallback">默认实例（文件缺失/损坏时返回）。</param>
    /// <param name="log">失败留痕出口（可空）。</param>
    /// <returns>装载结果（失败即默认值）。</returns>
    public static T LoadFile<T>(string baseDirectory, string sectionName, Func<string, T> parse, T fallback, Action<string>? log)
    {
        string path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return fallback;
        }

        try
        {
            return parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[host] appsettings.json 装载失败，{sectionName} 回退全默认：{ex.Message}");
            return fallback;
        }
    }
}
