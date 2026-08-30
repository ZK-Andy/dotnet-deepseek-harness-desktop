using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop;

/// <summary>DeepSeek Harness Desktop 入口：Ryn 桌面壳 + 托管 dsh 运行时 + 崩溃监督。</summary>
public static class Program
{
    /// <summary>
    /// 壳启动流程：托管 dsh web（OS 分配端口）→ 解析 `dsh web:` URL → Ryn WebView 加载；
    /// 后台监督 dsh 子进程——崩溃只重启子进程并导航新 URL（不重启桌面进程）；dsh 起不来时降级加载本地 wwwroot。
    /// 组合根编排见 <see cref="DesktopBootstrap"/>；本方法仅处理 CLI 专用路径并委托。
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // 无 UI 兜底诊断导出（ADR shell-observability-diagnostics）：先于一切启动逻辑——
        // 不 spawn dsh、不开窗、不做 dev 隔离，覆盖「闪退进不了界面」的取证场景。
        // CLI 形态下 stdout 可见；失败以非零退出码 fail loud（脚本可判定）。
        if (Array.IndexOf(args, "--export-diagnostics") >= 0)
        {
            return ExportDiagnostics();
        }

        return new DesktopBootstrap().Run();
    }

    /// <summary>CLI 专用：导出诊断包（不触 UI/运行时）；失败以非零退出码 fail loud。</summary>
    private static int ExportDiagnostics()
    {
        try
        {
            DiagnosticsExportResult result = DiagnosticsExporter.ExportWithFallback(
                HarnessRuntimeHost.ResolveDshHome(),
                Services.Update.AppVersion.Current());
            // CLI 形态下 stdout 可见；经 HostLog 双写让桌面形态的同一动作也落 host.log
            Services.HostLog.Write($"[host] 诊断包已导出：{result.ZipPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] 诊断包导出失败：{ex.Message}");
            return 1;
        }
    }
}
