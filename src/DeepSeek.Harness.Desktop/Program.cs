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
                opts.ApplicationId = "io.github.ZK-Andy.dotnet-deepseek-harness-desktop";
                var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
                if (File.Exists(iconPath))
                {
                    opts.IconPath = iconPath;
                }
                else
                {
                    Console.WriteLine($"[host] icon 缺失：{iconPath}");
                }

                Console.WriteLine($"[host] Ryn opts: Url={(webUrl is not null ? webUrl.ToString() : "null")} ApplicationId={opts.ApplicationId} Icon={(File.Exists(iconPath) ? iconPath : "missing")}");
                // WebView 调试器默认关闭（正式打包无调试窗口）；开发期设 DSH_DEVTOOLS=1 开启。
                opts.DevTools = Environment.GetEnvironmentVariable("DSH_DEVTOOLS") == "1";
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
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

        // 市场后台预装：窗口先亮（dsh web: 已就绪），再在后台静默安装 dshmarket，装完自动刷新（不阻塞首启）
        // 对齐 Tauri 的“推荐预设”后台装 + pilot-harness 的随包 theme 已在 node_modules
        // 0.1.10 失败复盘：tgz 为 394B 假包（app）+ pnpm allowBuilds 未开致 ERR_PNPM_IGNORED_BUILDS + 检测/补 bundles 未落地
        if (webUrl is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    var dshHome = HarnessRuntimeHost.ResolveDshHome();
                    var profileDir = Path.Combine(dshHome, "profiles", "web");
                    var profilePkg = Path.Combine(profileDir, "package.json");
                    var workspacePath = Path.Combine(profileDir, "pnpm-workspace.yaml");

                    // 1) 精确检测是否已装（JSON 解析，非字符串包含）
                    if (MarketInstallHelper.IsMarketInstalled(profilePkg))
                    {
                        Console.WriteLine("[host] 市场已就位（bundles 含 dshmarket），跳过安装");
                        return;
                    }

                    // 1b) 迁移：清理 0.1.8-0.1.10 误写入的 app 依赖（file:.../dshmarket.tgz 假包）
                    await MarketInstallHelper.CleanupBogusAppDependencyAsync(profilePkg);

                    var runtimeDir = RuntimeLocator.ResolveRuntimeDirectory();
                    var bundled = RuntimeLocator.TryLocateBundled(runtimeDir);
                    if (bundled is null)
                    {
                        Console.WriteLine("[host] 未找到捆绑运行时，跳过市场安装");
                        return;
                    }

                    // 2) 确保 pnpm-workspace.yaml 的 allowBuilds 已放行原生构建（pnpm 11 默认拒绝）
                    MarketInstallHelper.EnsureWorkspaceAllowBuilds(workspacePath);

                    // 3) 解析安装 spec：优先校验过的 tgz，否则回退到目录或 registry
                    var spec = MarketInstallHelper.ResolveMarketSpec(runtimeDir);
                    Console.WriteLine($"[host] 市场安装 spec={spec}");

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = bundled.Value.NodeExe,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    psi.ArgumentList.Add(bundled.Value.DshEntry);
                    psi.ArgumentList.Add("plugin");
                    psi.ArgumentList.Add("--profile");
                    psi.ArgumentList.Add("web");
                    psi.ArgumentList.Add("add");
                    psi.ArgumentList.Add(spec);
                    psi.Environment["DSH_HOME"] = dshHome;
                    psi.Environment["pnpm_config_store_dir"] = Path.Combine(dshHome, ".pnpm-store");
                    psi.Environment["pnpm_config_cache_dir"] = Path.Combine(dshHome, ".pnpm-cache");
                    // 兼容旧 pnpm 的 store 仍被读取时不因 EROFS 失败（现 DSH_HOME 已可写，但保留注入）
                    Directory.CreateDirectory(Path.Combine(dshHome, ".pnpm-store"));
                    Directory.CreateDirectory(Path.Combine(dshHome, ".pnpm-cache"));
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p is null)
                    {
                        Console.WriteLine("[host] 无法启动 dsh plugin 进程");
                        return;
                    }

                    var stdout = await p.StandardOutput.ReadToEndAsync();
                    var stderr = await p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    Console.WriteLine($"[host] dsh plugin add exit={p.ExitCode} stdout={stdout.Trim()} stderr={stderr.Trim()}");
                    if (p.ExitCode == 0)
                    {
                        // 4) dsh 的 reconcilePlugins 理论上已写入 bundles，仍做兜底校验并补写（0.1.10 的 file:app 未进 bundles 需补）
                        var patched = await MarketInstallHelper.EnsureBundlesContainsMarketAsync(profilePkg);
                        if (patched)
                        {
                            Console.WriteLine("[host] 已补写 bundles dshmarket");
                        }

                        Console.WriteLine("[host] dsh-market 已后台安装，重启运行时以加载市场");
                        try
                        {
                            await windowAccessor.Current.EvaluateJavaScriptAsync(RecoveryScript);
                        }
                        catch
                        {
                        }

                        // 仅刷新 WebView 不会让服务端重载 package.json，交由 RuntimeSupervisor 重启 dsh 进程并导航新 URL
                        // 此处直接 Stop，Supervisor 的 RunAsync 会检测退出→RestartAsync→Navigate
                        try
                        {
                            host.Stop();
                            Console.WriteLine("[host] 已触发 dsh 重启（由监督器接管）");
                        }
                        catch (Exception ex2)
                        {
                            Console.WriteLine($"[host] 触发重启失败：{ex2.Message}");
                            try
                            {
                                var newUrl = await host.RestartAsync(TimeSpan.FromSeconds(60));
                                if (newUrl is not null)
                                {
                                    await windowAccessor.Current.NavigateAsync(newUrl);
                                }
                                else
                                {
                                    await windowAccessor.Current.NavigateAsync(webUrl);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[host] dsh-market 安装失败 exit={p.ExitCode}，请查看 stderr 的 allowBuilds 提示");
                        // 常见失败：ERR_PNPM_IGNORED_BUILDS 时，workspace 已在 EnsureWorkspaceAllowBuilds 中修过，下次启动会自愈
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[host] 市场后台安装跳过：{ex.Message}");
                }
            });
        }

        Console.WriteLine("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            app.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[host] Ryn Run 异常：{ex}");
        }

        Console.WriteLine("[host] Ryn Run 结束");
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
