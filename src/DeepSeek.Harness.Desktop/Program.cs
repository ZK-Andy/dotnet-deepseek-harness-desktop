using System.Runtime.InteropServices;
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
        // 开发运行时完全隔离（ADR dev-runtime-isolation）：ApplicationId 加 .dev 后缀避开
        // GTK 同 id 单实例互斥（与已装正式版可同时开窗）；DSH_HOME 未显式覆盖时自动指向
        // 仓库 .cache/dev-home，杜绝与正式版共享 profile 的串扰。
        // dev 判定：显式 runtime 覆盖，或定位不到捆绑闭包（dotnet run 的 PATH dsh 回退形态）。
        var updateRuntimeDir = RuntimeLocator.ResolveRuntimeDirectory();
        var bundledClosure = RuntimeLocator.TryLocateBundled(updateRuntimeDir);
        var devRuntimeDir = Environment.GetEnvironmentVariable(DevEnvironment.RuntimeDirEnv);
        var isDev = DevEnvironment.IsDevRuntime(devRuntimeDir, bundledClosure is not null);
        var devAutoIsolated = false;
        if (isDev && Environment.GetEnvironmentVariable(DevEnvironment.HomeOverrideEnv) is null)
        {
            var devHome = DevEnvironment.DeriveDefaultDevHome(devRuntimeDir, AppContext.BaseDirectory);
            if (devHome is not null)
            {
                Environment.SetEnvironmentVariable(DevEnvironment.HomeOverrideEnv, devHome);
                devAutoIsolated = true;
                Console.WriteLine($"[host] 开发运行时：DSH_HOME 隔离到 {devHome}；ApplicationId 带 .dev 后缀，可与正式版并存");
            }
        }

        using var host = new HarnessRuntimeHost(bundledClosure);
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

        // 自更新栈（仅 ready 对外可见；机制见 ADR desktop-shell-self-update）：
        // 状态机纯逻辑可单测，检查/下载/安装全部委托注入；状态经 CustomEvent 推给插件 UI。
        var updateOptions = Services.Update.UpdateOptions.Load(AppContext.BaseDirectory);
        var updateHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var updatesDir = Path.Combine(HarnessRuntimeHost.ResolveDshHome(), updateOptions.UpdatesDirName);
        var updatePkgKind = Services.Update.UpdatePlatform.DetectCurrentPackageKind();
        CurrentWindowAccessor? updateWindow = null;
        var updateMachine = new Services.Update.UpdateStateMachine(
            currentVersion: Services.Update.AppVersion.Current(),
            check: ct => new Services.Update.ReleaseMetaClient(updateHttp, updateOptions).FetchLatestAsync(UpdateRid(), updatePkgKind, ct),
            download: (meta, ct) => new Services.Update.InstallerDownloader(updateHttp).DownloadAsync(
                meta, updatesDir, TimeSpan.FromMinutes(updateOptions.DownloadTimeoutMinutes), ct),
            install: (assetPath, _, ct) => Services.Update.UpdateInstaller.LaunchAsync(assetPath, updatesDir, ct),
            persistence: new Services.Update.FileReadyPersistence(updatesDir),
            onTransition: state => PushUpdateState(updateWindow, state));
        Console.WriteLine($"[host] 自更新：当前版本 {Services.Update.AppVersion.Current()}，RID {UpdateRid()}，包类型 {updatePkgKind ?? "(n/a)"}，目录 {updatesDir}");

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
                opts.ApplicationId = DevEnvironment.ApplicationIdFor(
                    "io.github.ZK-Andy.dotnet-deepseek-harness-desktop", isDev);
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
                // 外部链接 → 系统默认浏览器（宿主命令路由，见 proposed bug-fix ADR）
                services.AddSingleton<ICommandRouter, Services.ExternalLinkCommandRouter>();
                // 自更新命令：desktop.update.getState / check / install
                services.AddSingleton<ICommandRouter>(new Services.Update.DesktopUpdateCommandRouter(updateMachine));
            })
            .Build();

        var windowAccessor = app.Services.GetRequiredService<CurrentWindowAccessor>();
        updateWindow = windowAccessor;
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

        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        _ = Task.Run(async () =>
        {
            try
            {
                await updateMachine.StartAsync(supervisorCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[update] start 失败：{ex.Message}");
            }
        });

        // 市场后台预装：窗口先亮（dsh web: 已就绪），再在后台静默安装随包插件，装完自动刷新（不阻塞首启）
        // 对齐 Tauri 的“推荐预设”后台装 + pilot-harness 的随包 theme 已在 node_modules
        // 0.1.10 失败复盘：tgz 为 394B 假包（app）+ pnpm allowBuilds 未开致 ERR_PNPM_IGNORED_BUILDS + 检测/补 bundles 未落地
        // 随包插件：dshmarket（市场）+ dsh-desktop-companion（桌面伴生：外部链接接管等，见 ADR desktop-shell-companion-plugin）
        if (webUrl is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));

                    // 开发运行时：仅当 home 已自动隔离（devAutoIsolated）才允许随包安装——
                    // 隔离 home 与正式版无涉，装了市场/伴生 debug 才完整；
                    // 用户显式把 DSH_DESKTOP_DSH_HOME 指回真实 home 时仍跳过（防串扰，2026-08-22 实证）。
                    if (isDev && !devAutoIsolated)
                    {
                        Console.WriteLine("[host] 开发运行时且 DSH_HOME 为显式覆盖，跳过随包插件安装以防污染共享 profile");
                        return;
                    }

                    var dshHome = HarnessRuntimeHost.ResolveDshHome();
                    var profileDir = Path.Combine(dshHome, "profiles", "web");
                    var profilePkg = Path.Combine(profileDir, "package.json");
                    var workspacePath = Path.Combine(profileDir, "pnpm-workspace.yaml");

                    var runtimeDir = RuntimeLocator.ResolveRuntimeDirectory();
                    var bundled = RuntimeLocator.TryLocateBundled(runtimeDir);
                    if (bundled is null)
                    {
                        Console.WriteLine("[host] 未找到捆绑运行时，跳过随包插件安装");
                        return;
                    }

                    // 1) 精确检测未就位的随包插件（JSON 解析，非字符串包含）
                    var pending = new List<(string Package, string Spec)>();
                    if (!MarketInstallHelper.IsBundleInstalled(profilePkg, "dshmarket"))
                    {
                        pending.Add(("dshmarket", MarketInstallHelper.ResolveMarketSpec(runtimeDir)));
                    }

                    var companionSpec = MarketInstallHelper.ResolveCompanionSpec(runtimeDir);
                    if (companionSpec is not null &&
                        !MarketInstallHelper.IsBundleInstalled(profilePkg, "dsh-desktop-companion"))
                    {
                        pending.Add(("dsh-desktop-companion", companionSpec));
                    }

                    if (pending.Count == 0)
                    {
                        Console.WriteLine("[host] 随包插件已就位（bundles 含全部待装项），跳过安装");
                        return;
                    }

                    // 1b) 迁移：清理 0.1.8-0.1.10 误写入的 app 依赖（file:.../dshmarket.tgz 假包）
                    await MarketInstallHelper.CleanupBogusAppDependencyAsync(profilePkg);

                    // 2) 确保 pnpm-workspace.yaml 的 allowBuilds 已放行原生构建（pnpm 11 默认拒绝）
                    MarketInstallHelper.EnsureWorkspaceAllowBuilds(workspacePath);

                    foreach (var (_, spec) in pending)
                    {
                        Console.WriteLine($"[host] 随包插件安装 spec={spec}");
                    }

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
                    foreach (var (_, spec) in pending)
                    {
                        psi.ArgumentList.Add(spec);
                    }

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
                        foreach (var (pkg, _) in pending)
                        {
                            if (await MarketInstallHelper.EnsureBundlesContainsAsync(profilePkg, pkg))
                            {
                                Console.WriteLine($"[host] 已补写 bundles {pkg}");
                            }
                        }

                        Console.WriteLine("[host] 随包插件已后台安装，重启运行时以加载");
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
                        Console.WriteLine($"[host] 随包插件安装失败 exit={p.ExitCode}，请查看 stderr 的 allowBuilds 提示");
                        // 常见失败：ERR_PNPM_IGNORED_BUILDS 时，workspace 已在 EnsureWorkspaceAllowBuilds 中修过，下次启动会自愈
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[host] 随包插件后台安装跳过：{ex.Message}");
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

        /// <summary>当前平台的更新资产 RID（与 release 资产命名后缀对应）。</summary>
        static string UpdateRid()
        {
            if (OperatingSystem.IsLinux())
            {
                return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            }

            if (OperatingSystem.IsWindows())
            {
                return "win-x64";
            }

            if (OperatingSystem.IsMacOS())
            {
                return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            }

            return "unknown";
        }

        /// <summary>把状态变化推给页面：插件监听 <c>dsh-desktop-update</c> CustomEvent 渲染更新按钮。</summary>
        static void PushUpdateState(CurrentWindowAccessor? accessor, Services.Update.UpdateState state)
        {
            try
            {
                // Current 在窗口未创建/已关闭时抛异常（非返回 null）：启动早期与退出阶段都会走到
                if (accessor?.Current is null)
                {
                    return;
                }

                _ = accessor.Current.EvaluateJavaScriptAsync(
                    "(function(){try{document.dispatchEvent(new CustomEvent('dsh-desktop-update',{detail:"
                    + state.ToJson()
                    + "}));}catch(e){}})();");
            }
            catch (InvalidOperationException)
            {
                // 窗口尚未就绪：本次推送丢弃，后续状态变化会再推
            }
        }
    }
}
