namespace DeepSeek.Harness.Desktop;

/// <summary>DeepSeek Harness Desktop 入口：Avalonia 宿主（G0，ADR avalonia-g0-entry-switch）+ 托管 dsh 运行时；崩溃监督与首启引导随迁移片接回。
/// CLI 专用路径（诊断导出）的判定与实现住 Infrastructure <c>DiagnosticsCli</c>（细搬后入口只委托）。</summary>
public static class Program
{
    /// <summary>
    /// 壳启动流程：托管 dsh web（OS 分配端口）→ 解析 `dsh web:` URL → WebView 加载壳 origin；
    /// 迁移期（G0）不接崩溃监督与 wwwroot 降级，宿主接线见 <see cref="Hosting.AvaloniaHost"/>。
    /// 无 UI 诊断导出先于一切启动逻辑
    /// （不 spawn dsh、不开窗、不做 dev 隔离，ADR shell-observability-diagnostics）。
    /// </summary>
    [STAThread]
    public static int Main(string[] args) =>
        DiagnosticsCli.TryRun(args) ?? Hosting.AvaloniaHost.Run(args);
}
