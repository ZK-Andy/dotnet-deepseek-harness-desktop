using DeepSeek.Harness.Desktop.Commands;
using DeepSeek.Harness.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>DeepSeek Harness Desktop 入口：Ryn 桌面壳 + 托管 dsh 运行时 + 崩溃监督。</summary>
public static class Program
{
    // 恢复屏：一行 JS，覆写当前 WebView 文档为"重连中"（dsh 崩溃时展示；恢复后 NavigateAsync 整页回真实 UI）
    private const string RecoveryScript =
        "document.documentElement.innerHTML='<!doctype html><html><head><meta charset=utf-8><style>" +
        "body{font-family:system-ui,sans-serif;background:#0f0f13;color:#e6e6ea;display:flex;flex-direction:column;align-items:center;justify-content:center;height:100vh;gap:12px}" +
        ".spin{width:36px;height:36px;border:3px solid #2a2a3a;border-top-color:#7c3aed;border-radius:50%;animation:r 1s linear infinite}" +
        "@keyframes r{to{transform:rotate(360deg)}}</style></head>" +
        "<body><div class=spin></div><h2>DeepSeek Harness Desktop</h2><p>运行时重启中，正在重新连接…</p></body></html>';";

    /// <summary>
    /// 壳启动流程：托管 dsh web（OS 分配端口）→ 解析 `dsh web:` URL → Ryn WebView 加载；
    /// 后台监督 dsh 子进程——崩溃只重启子进程并导航新 URL（不重启桌面进程）；dsh 起不来时降级加载本地 wwwroot。
    /// </summary>
    [STAThread]
    public static void Main()
    {
        using var host = new HarnessRuntimeHost(RuntimeLocator.TryLocateBundled(RuntimeLocator.ResolveRuntimeDirectory()));
        var webUrl = host.StartAsync(timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        Console.WriteLine($"[host] runtime = {host.RuntimeDescription}");
        if (webUrl is not null)
        {
            Console.WriteLine($"[host] dsh web = {webUrl}");
        }
        else
        {
            Console.WriteLine($"[host] dsh 未在时限内给出 URL；降级加载 wwwroot。stderr 尾巴：\n{string.Join('\n', host.StderrTail.TakeLast(8))}");
        }

        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                if (webUrl is not null)
                {
                    // dsh web UI（loopback；完整运行时随应用内置后仍是此路径）
                    opts.Url = webUrl;
                }
                else
                {
                    // 降级：dsh 未起时展示本地占位页，保证壳仍可开
                    opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                }

                opts.Title = "DeepSeek Harness Desktop";
                opts.Width = 1200;
                opts.Height = 800;
                var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
                if (File.Exists(iconPath))
                {
                    opts.IconPath = iconPath;
                }
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
                services.AddAppCommands();
            })
            .Build();

        var windowAccessor = app.Services.GetRequiredService<CurrentWindowAccessor>();
        using var supervisorCts = new CancellationTokenSource();
        var supervisor = new RuntimeSupervisor(
            host,
            restartTimeout: TimeSpan.FromSeconds(60),
            showRecovery: () =>
            {
                _ = windowAccessor.Current.EvaluateJavaScriptAsync(RecoveryScript);
                return ValueTask.CompletedTask;
            },
            navigate: url => windowAccessor.Current.NavigateAsync(url),
            log: Console.WriteLine);
        var supervisorTask = Task.Run(() => supervisor.RunAsync(supervisorCts.Token));

        app.Run();

        supervisorCts.Cancel();
        try
        {
            supervisorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 监督任务随宿主回收而结束；无需上报
        }

        host.Stop();
    }
}
