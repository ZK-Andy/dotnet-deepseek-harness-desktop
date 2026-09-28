namespace DeepSeek.Harness.Desktop;

/// <summary>DeepSeek Harness Desktop 入口：Ryn 桌面壳 + 托管 dsh 运行时 + 崩溃监督。
/// CLI 专用路径（诊断导出）的判定与实现住 Infrastructure <c>DiagnosticsCli</c>（细搬后入口只委托）。</summary>
public static class Program
{
    /// <summary>
    /// 壳启动流程：托管 dsh web（OS 分配端口）→ 解析 `dsh web:` URL → Ryn WebView 加载；
    /// 后台监督 dsh 子进程——崩溃只重启子进程并导航新 URL（不重启桌面进程）；dsh 起不来时降级加载本地 wwwroot。
    /// 组合根编排见 <see cref="DesktopBootstrap"/>。无 UI 诊断导出先于一切启动逻辑
    /// （不 spawn dsh、不开窗、不做 dev 隔离，ADR shell-observability-diagnostics）。
    /// </summary>
    [STAThread]
    public static int Main(string[] args) =>
        DiagnosticsCli.TryRun(args) ?? new DesktopBootstrap().Run();
}
