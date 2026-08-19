using DeepSeek.Harness.Desktop.Commands;
using DeepSeek.Harness.Desktop.Services;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>DeepSeek Harness Desktop 入口：Ryn 桌面壳 + 托管 dsh 运行时。</summary>
public static class Program
{
    /// <summary>
    /// 壳启动流程：托管 dsh web（OS 分配端口）→ 解析 `dsh web:` URL → Ryn WebView 加载该 URL；
    /// dsh 未能在时限内给出 URL 时降级加载本地 wwwroot 占位页。
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
                    // dsh web UI（loopback；未来完整运行时随应用内置后仍是此路径）
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
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
                services.AddAppCommands();
            })
            .Build();

        app.Run();
    }
}
