namespace DeepSeek.Harness.Desktop.Infrastructure.Update;

/// <summary>自更新链路的 HTTP 出口（Infrastructure 边界）：全仓唯一的更新链路客户端来源，
/// 消费者只有自更新栈的两家适配器（feed 抓取与安装包下载）。
/// </summary>
internal static class UpdateHttpClient
{
    /// <summary>进程级共享客户端：单一实例、无整体超时——feed 抓取与安装包下载各自经调用方限时，
    /// 整体超时会误杀分钟级下载。
    /// <para>构造点只有这一处：客户端直接在本属性的初始化里造，不再有第二个构造入口（工厂方法已取消——
    /// 工厂若可被外部调用，「连接池 1→2」就不由可见性兜底了；ADR
    /// architecture/2026-10-20-update-stack-adapter-split）。</para></summary>
    internal static HttpClient Shared { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };
}
