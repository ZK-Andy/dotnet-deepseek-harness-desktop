using DeepSeek.Harness.Desktop.Commands;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop;

/// <summary>DeepSeek Harness Desktop 入口：Ryn 桌面壳。</summary>
public static class Program
{
    /// <summary>Ryn 壳在原生窗口加载 wwwroot 下的 Web UI（未来为 dsh Web UI）。</summary>
    [STAThread]
    public static void Main()
    {
        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
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
