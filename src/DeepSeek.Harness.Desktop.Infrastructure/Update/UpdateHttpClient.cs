namespace DeepSeek.Harness.Desktop.Infrastructure.Update;

/// <summary>自更新链路的共享 HTTP 客户端工厂（Infrastructure 边界）：单一实例、无整体超时——
/// feed 抓取与安装包下载各自经调用方限时，整体超时会误杀分钟级下载。</summary>
public static class UpdateHttpClient
{
    /// <summary>创建自更新专用 <see cref="HttpClient"/>（<c>Timeout = InfiniteTimeSpan</c>）。</summary>
    public static HttpClient Create() => new() { Timeout = Timeout.InfiniteTimeSpan };
}
