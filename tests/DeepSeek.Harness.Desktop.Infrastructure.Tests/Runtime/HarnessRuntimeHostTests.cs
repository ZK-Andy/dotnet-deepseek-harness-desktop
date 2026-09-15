using System.ComponentModel;
using System.Net;
using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>HarnessRuntimeHost 集成冒烟：真实 spawn dsh → 解析 URL。门控：设 DSH_TEST_E2E=1 且环境里有 dsh 才断言，否则自跳过。</summary>
/// <remarks>环境变量型测试与 <see cref="SharedHomeContractTests"/> 同集合串行——
/// 两者都改写进程级 DSH_HOME 覆盖变量，并行会互相污染（实测 flaky 教训）。</remarks>
[Collection("dsh-home-env")]
public class HarnessRuntimeHostTests
{
    /// <summary>验证取消令牌已置位时 StartAsync 直接返回 null：入口取消检查先于 spawn，任何路径都不会留下无人认领的 dsh 孤儿子进程。</summary>
    [Fact]
    public async Task StartAsync_CancelledToken_ReturnsNull_WithoutSpawning()
    {
        // 取消是终态：入口检查先于 spawn，监督器恢复分支撞上退出时绝不留下无人认领的 dsh 孤儿。
        using var host = new HarnessRuntimeHost();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await host.StartAsync(TimeSpan.FromSeconds(5), cts.Token));
    }

    /// <summary>验证 BuildDshWebArgs 产物含 --no-open 且第二参数为桌面 profile 名，避免 dsh web 把 URL 交给系统默认浏览器、与内嵌 WebView 重复弹窗。</summary>
    [Fact]
    public void BuildDshWebArgs_IncludesNoOpen_SoShellDoesNotHandOffToOsBrowser()
    {
        // rc.8+ 的 dsh web 默认 openBrowser=true，会把 URL 交给 OS 默认浏览器；
        // 桌面壳自渲染内嵌 WebView，必须传 --no-open 避免与桌面窗口重复弹出。
        string[] args = HarnessRuntimeHost.BuildDshWebArgs(0);
        Assert.Contains("--no-open", args);
        Assert.Equal(HarnessRuntimeHost.DesktopProfileName, args[1]);
    }

    /// <summary>验证当前 PATH 为 null 或空串时，富化结果仅含 home 目录下 ~/.local/bin 单独一段（按 ':' 拼接）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildEnrichedPath_EmptyOrNullPath_BecomesLocalBinOnly(string? current)
    {
        Assert.Equal("/home/u/.local/bin", HarnessRuntimeHost.BuildEnrichedPath(current, "/home/u", ':'));
    }

    /// <summary>验证 spawn 环境注入血统 token 与生效 home：血统扫描、孤儿清扫、端口交接处置全依赖这两项。</summary>
    [Fact]
    public void BuildStartPsi_CarriesLineageTokenAndHome()
    {
        System.Diagnostics.ProcessStartInfo psi = HarnessRuntimeHost.BuildStartPsi(port: 0, home: "/home/u/.dsh", spawnToken: "token-xyz");

        Assert.Equal("token-xyz", psi.Environment[RuntimeLineage.TokenEnv]);
        Assert.Equal("/home/u/.dsh", psi.Environment["DSH_HOME"]);
    }

    /// <summary>验证 ~/.local/bin 未出现在既有 PATH 中时被追加到末尾，原有各段相对顺序保持不变。</summary>
    [Fact]
    public void BuildEnrichedPath_MissingLocalBin_AppendsAfterExisting()
    {
        string enriched = HarnessRuntimeHost.BuildEnrichedPath("/usr/local/bin:/usr/bin", "/home/u", ':');
        Assert.Equal("/usr/local/bin:/usr/bin:/home/u/.local/bin", enriched);
    }

    /// <summary>验证 ~/.local/bin 已在 PATH 中时富化结果原样返回，不产生重复段（幂等）。</summary>
    [Fact]
    public void BuildEnrichedPath_AlreadyPresent_Idempotent()
    {
        const string path = "/home/u/.local/bin:/usr/bin";
        Assert.Equal(path, HarnessRuntimeHost.BuildEnrichedPath(path, "/home/u", ':'));
    }

    /// <summary>验证 ~/.local/bin-extra 这类相似前缀段不会被误判为已含 ~/.local/bin，仍会追加本尊目录。</summary>
    [Fact]
    public void BuildEnrichedPath_SimilarPrefixSegment_DoesNotFalsePositive()
    {
        // /home/u/.local/bin-extra 不是 ~/.local/bin 本尊，不得据此判已含。
        string enriched = HarnessRuntimeHost.BuildEnrichedPath("/home/u/.local/bin-extra", "/home/u", ':');
        Assert.Equal("/home/u/.local/bin-extra:/home/u/.local/bin", enriched);
    }

    /// <summary>DSH_TEST_E2E=1 且 PATH 中有 dsh 时真实 spawn dsh 并断言解析出的 URL 形如 http://127.0.0.1:&lt;port&gt;：未启用或找不到 dsh 时自动跳过并保持绿色。</summary>
    [Fact]
    public async Task StartAsync_ParsesRealDshWebUrl_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        string home = Path.Combine(Path.GetTempPath(), "dsh-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");
        try
        {
            // 新 home 无 profile 时 dsh 立即退出（拿不到 URL）——先做 profile 引导（入 try 保证环境清理）
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));
            using var host = new HarnessRuntimeHost();
            Uri? url;
            try
            {
                url = await host.StartAsync(TimeSpan.FromSeconds(30));
            }
            catch (Win32Exception)
            {
                // PATH 里没有 dsh——跳过
                return;
            }

            host.Stop();
            Assert.NotNull(url);
            Assert.StartsWith("http://127.0.0.1:", url!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证子进程被终止后 RestartAsync 能重建新实例且 origin 保持不变（端口相等）——Web UI 才能记住上一会话；launch token 每进程必新。</summary>
    [Fact]
    public async Task RestartAsync_AfterChildKilled_YieldsNewUrl_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        string home = Path.Combine(Path.GetTempPath(), "dsh-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");
        try
        {
            // 新 home 无 profile 时 dsh 立即退出（拿不到 URL）——先做 profile 引导（入 try 保证环境清理）
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));
            using var host = new HarnessRuntimeHost();
            Uri? first;
            try
            {
                first = await host.StartAsync(TimeSpan.FromSeconds(30));
            }
            catch (Win32Exception)
            {
                return; // PATH 没有 dsh——跳过
            }

            Assert.NotNull(first);
            Task exit = host.WaitForExitAsync();
            host.Stop(); // 模拟子进程被终止
            await exit.WaitAsync(TimeSpan.FromSeconds(5));

            Uri? restarted = await host.RestartAsync(TimeSpan.FromSeconds(30));
            host.Stop();

            Assert.NotNull(restarted);
            // 稳定端口：重启后 origin 不变，Web UI 才能记住上一会话；per-process launch token 每次必新，不参与断言
            Assert.Equal(first!.GetLeftPart(UriPartial.Authority), restarted.GetLeftPart(UriPartial.Authority));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证 DSH_HOME 下端口状态文件读写往返：初始无文件时 TryLoadPersistedPort 为 null，PersistPort 写入后按原值读回。</summary>
    [Fact]
    public void PersistPort_ThenLoad_RoundTripsUnderDshHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        try
        {
            // 初始无状态文件 → null
            Assert.Null(HarnessRuntimeHost.TryLoadPersistedPort());

            HarnessRuntimeHost.PersistPort(4242);
            Assert.Equal(4242, HarnessRuntimeHost.TryLoadPersistedPort());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证 0.3.5 及之前写在 home 根的历史端口文件，在 profile 文件缺失时仍被回读沿用，升级后首次启动零感知。</summary>
    [Fact]
    public void LegacyHomeRootFile_UsedWhenProfileFileMissing()
    {
        // 迁移回读：0.3.5 及之前把端口记忆写在 home 根；升级后首次启动应零感知沿用
        string home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        try
        {
            File.WriteAllText(HarnessRuntimeHost.ResolveLegacyPortFilePath(), "46777");
            Assert.Equal(46777, HarnessRuntimeHost.TryLoadPersistedPort());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证 PersistPort 只写 profile 路径下的端口文件，绝不回写 home 根旧位置文件，跨 profile 争抢不能借尸还魂。</summary>
    [Fact]
    public void Persist_WritesProfilePathOnly_LegacyFileUntouched()
    {
        // 写入绝不回流旧位置：跨 profile 争抢不能借尸还魂
        string home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        try
        {
            File.WriteAllText(HarnessRuntimeHost.ResolveLegacyPortFilePath(), "1111");
            HarnessRuntimeHost.PersistPort(4242);

            Assert.Equal("4242", File.ReadAllText(HarnessRuntimeHost.ResolvePortFilePath()));
            Assert.Equal("1111", File.ReadAllText(HarnessRuntimeHost.ResolveLegacyPortFilePath()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证 profile 文件缺失且 home 根旧文件内容非数字时按无记忆返回 null，不抛异常阻断启动。</summary>
    [Fact]
    public void ProfileFile_MissingButLegacyCorrupt_ReturnsNull()
    {
        // 旧文件损坏同样按无记忆处理，不得抛出阻断启动
        string home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        try
        {
            File.WriteAllText(HarnessRuntimeHost.ResolveLegacyPortFilePath(), "not-a-number");
            Assert.Null(HarnessRuntimeHost.TryLoadPersistedPort());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证 profile 端口文件内容损坏时 TryLoadPersistedPort 返回 null，且随后的 PersistPort 能覆盖修复为新值。</summary>
    [Fact]
    public void TryLoadPersistedPort_CorruptFile_ReturnsNull()
    {
        string home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "profiles", HarnessRuntimeHost.DesktopProfileName));
            File.WriteAllText(HarnessRuntimeHost.ResolvePortFilePath(), "not-a-number");
            Assert.Null(HarnessRuntimeHost.TryLoadPersistedPort());

            HarnessRuntimeHost.PersistPort(4343);
            Assert.Equal(4343, HarnessRuntimeHost.TryLoadPersistedPort()); // 覆盖修复
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证全新实例（端口缓存为空）冷启动时从磁盘加载上次端口，两次实例解析出相同 URL，origin 保持不变。</summary>
    [Fact]
    public async Task StartAsync_PersistsPort_AcrossFreshInstances_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        string home = Path.Combine(Path.GetTempPath(), "dsh-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");
        try
        {
            // 新 home 无 profile 时 dsh 立即退出（拿不到 URL）——先做 profile 引导（入 try 保证环境清理）
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));
            Uri? first;
            using (var firstHost = new HarnessRuntimeHost())
            {
                try
                {
                    first = await firstHost.StartAsync(TimeSpan.FromSeconds(30));
                }
                catch (Win32Exception)
                {
                    return; // PATH 没有 dsh——跳过
                }

                firstHost.Stop();
            }

            Assert.NotNull(first);

            // 新实例即"整 App 冷启动"（_port 为空）：应从磁盘加载上次端口 → 同 URL（origin 不变）
            using var secondHost = new HarnessRuntimeHost();
            try
            {
                Uri? second = await secondHost.StartAsync(TimeSpan.FromSeconds(30));
                secondHost.Stop();
                Assert.NotNull(second);
                // origin 不变（同冷启动端口记忆）；per-process launch token 每次必新，不参与断言
                Assert.Equal(first!.GetLeftPart(UriPartial.Authority), second.GetLeftPart(UriPartial.Authority));
            }
            catch (Win32Exception)
            {
                return; // PATH 没有 dsh——跳过（防御性）
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>验证首选端口被占时 spawn 前 bind 探测拦下注定失败的尝试（ADR port-wait-compression）：
    /// 日志出现「探测已被占」跳过行，启动经漂移回退成功且端口不等于被占端口——不经过 42–47s 的注定尝试。</summary>
    [Fact]
    public async Task StartAsync_PreferredPortProbeOccupied_SkipsDoomedSpawn_AndDrifts_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        string home = Path.Combine(Path.GetTempPath(), "dsh-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");

        // 占位者：bind 但不 accept——bind 探测必须连这种占用者也能判出（与连接探测互补）
        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        try
        {
            // 新 home 无 profile 时 dsh 立即退出（回退 spawn 拿不到 URL）——先做 profile 引导（入 try 保证环境清理）
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));
            int occupied = ((IPEndPoint)squatter.LocalEndpoint).Port;
            HarnessRuntimeHost.PersistPort(occupied);

            var logs = new List<string>();
            using var host = new HarnessRuntimeHost(logs.Add);
            Uri? url;
            try
            {
                url = await host.StartAsync(TimeSpan.FromSeconds(60));
            }
            catch (Win32Exception)
            {
                return; // PATH 没有 dsh——跳过
            }

            host.Stop();

            Assert.NotNull(url);
            Assert.NotEqual(occupied, url.Port);
            Assert.Contains(logs, l => l.Contains($"首选端口 {occupied} 探测已被占"));
            Assert.Contains(logs, l => l.Contains("漂移至"));
        }
        finally
        {
            squatter.Stop();
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>
    /// 市场接力收养闭环（Linux /proc 真探针回归，ADR market-restart-adopt-first）：被监督运行时「退出」后
    /// helper 进程在场、新生服务端已接管首选端口 → 接力等待窗口直接收养并返回收养 URL，全程不 spawn
    /// 竞争 dsh。假进程 = sh 携带血统 env（token + DSH_HOME），cmdline 分别按 helper 特征与服务端形状
    /// 构造；无 dsh 参与，无端口之争。非 Linux（无 /proc env 枚举）自跳过。
    /// </summary>
    [Fact]
    public async Task TryRideMarketRelayAsync_AdoptsLinuxLineageSuccessor_WhenRelayTakesOver()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 血统枚举靠 /proc environ；其他平台 Enumerate 恒空，接力路径不可达
        }

        var logs = new List<string>();
        string home = Path.Combine(Path.GetTempPath(), "dsh-relay-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, home);
        var processes = new List<System.Diagnostics.Process>();
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var responder = new CancellationTokenSource();
        (TcpListener listener, Task serving) = LoopbackHttpResponder.Start(
            port,
            LoopbackHttpResponder.Response("HTTP/1.1 401 Unauthorized", "dsh web authentication required; probe"),
            responder.Token);
        try
        {
            StartFakeRelayProcesses(home, "relay-test-token", processes);

            // 参照取自两个假进程诞生之前：可证「新生」
            DateTimeOffset supervisedStart = DateTimeOffset.UtcNow.AddSeconds(-1);
            using var host = new HarnessRuntimeHost(logs.Add);
            Uri? relayed = await host.TryRideMarketRelayAsync(
                port, supervisedStart, TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.NotNull(relayed);
            Assert.Equal(port, relayed!.Port);
            Assert.Contains(logs, l => l.Contains($"市场接力续任者已接管首选端口 {port}"));
            Assert.Contains(logs, l => l.Contains("收养市场接力的续任者"));
        }
        finally
        {
            responder.Cancel();
            await serving;
            listener.Stop();
            foreach (System.Diagnostics.Process p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.Dispose();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 假进程恰好已退出/无权限/平台不支持树杀：清理目标已达成
                }
            }

            Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>接力等待的诚实回落（注入口压缩时长，零进程；就绪判据走真探针）：无任何血统主体在场时窗口耗尽返回 null，绝不 spawn（spawn 由调用方既有路径负责）。</summary>
    [Fact]
    public async Task TryRideMarketRelayAsync_NoRelayEvidence_ExitsWindowWithNull()
    {
        var logs = new List<string>();
        using var host = new HarnessRuntimeHost(logs.Add)
        {
            RelayResidueOverride = () => [],
            RelayDelayOverride = _ => Task.CompletedTask,
        };

        Uri? relayed = await host.TryRideMarketRelayAsync(
            LoopbackHttpResponder.ReserveFreePort(), DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(relayed);
        Assert.DoesNotContain(logs, l => l.Contains("收养"));
    }

    /// <summary>
    /// R2 评审 Blocker 回归：临终 dsh 生前自拉起、继承 token 且父链已死的「孤儿定位主体」（PTC
    /// worker/pnpm 子壳，形状可判 RuntimeServer）不得充当接力证据——在场也只是按宽限窗回落，
    /// 绝不把崩溃恢复拖到等待预算上限（退出线③：额外等待 ≤ 一个宽限窗）。
    /// </summary>
    [Fact]
    public async Task TryRideMarketRelayAsync_OrphanTokenedChild_IsNotEvidence_BailsOnGrace()
    {
        var logs = new List<string>();
        using var host = new HarnessRuntimeHost(logs.Add)
        {
            // 孤儿子进程：出生晚于被监督运行时、形状可判服务端，但父 pid 已死——不是接力证据
            RelayResidueOverride = () =>
            {
                var orphan = new RuntimeLineage.Candidate(
                    999999, "orphan-token", "/home/u/.dsh",
                    $"node /usr/bin/dsh --profile {HarnessRuntimeHost.DesktopProfileName} --port 0",
                    DateTimeOffset.UtcNow);
                return (IReadOnlyList<RuntimeLineage.Subject>)new[] {
                    new RuntimeLineage.Subject(orphan, RuntimeLineage.LineageKind.RuntimeServer) };
            },
            RelayDelayOverride = _ => Task.CompletedTask,
        };

        var budget = TimeSpan.FromSeconds(15);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Uri? relayed = await host.TryRideMarketRelayAsync(
            LoopbackHttpResponder.ReserveFreePort(), DateTimeOffset.UtcNow.AddMinutes(-1), budget, CancellationToken.None);
        sw.Stop();

        Assert.Null(relayed);
        // 若孤儿被误判证据，循环会撑满 15s 预算；证据拒收则宽限窗（2s）内即回落
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"回落耗时 {sw.Elapsed}，孤儿被当成了接力证据");
        Assert.Contains(logs, l => l.Contains("无接力证据，宽限窗耗尽"));
        Assert.DoesNotContain(logs, l => l.Contains("收养"));
    }

    /// <summary>
    /// 取消回归（ADR relay-web-readiness）：接力窗口内的取消是终态——返回 null、不收养，也不留
    /// 「继续等待」的假就绪行（取消不是「web 面未就绪」）。端口有静默监听者，取消发生在 HTTP 探测中。
    /// </summary>
    [Fact]
    public async Task TryRideMarketRelayAsync_CancelledInWindow_ReturnsNullWithoutAdoptingOrFakeReadyLog()
    {
        var logs = new List<string>();
        var listener = new TcpListener(IPAddress.Loopback, LoopbackHttpResponder.ReserveFreePort());
        listener.Start();
        try
        {
            using var cts = new CancellationTokenSource();
            using var host = new HarnessRuntimeHost(logs.Add) { RelayResidueOverride = () => [] };
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));

            Uri? relayed = await host.TryRideMarketRelayAsync(
                ((IPEndPoint)listener.LocalEndpoint).Port,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                TimeSpan.FromSeconds(30),
                cts.Token);

            Assert.Null(relayed);
            Assert.DoesNotContain(logs, l => l.Contains("web 面未就绪"));
            Assert.DoesNotContain(logs, l => l.Contains("收养"));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// web 面未就绪回归（ADR relay-web-readiness，Linux /proc 真探针）：端口已可连、helper 证据在场，但
    /// HTTP 无声（续任者已 bind、web-runtime 行尚未挂载）——接力窗口必须继续等，绝不把 WebView 导航进
    /// 空白页；预算耗尽回落既有路径并留痕原因。非 Linux 自跳过。
    /// </summary>
    [Fact]
    public async Task TryRideMarketRelayAsync_SuccessorBoundButWebNotReady_KeepsWaitingToBudget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // 血统枚举靠 /proc environ；其他平台接力路径不可达
        }

        var logs = new List<string>();
        string home = Path.Combine(Path.GetTempPath(), "dsh-relay-webpending-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, home);
        var processes = new List<System.Diagnostics.Process>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            StartFakeRelayProcesses(home, "relay-test-token", processes);

            using var host = new HarnessRuntimeHost(logs.Add);
            Uri? relayed = await host.TryRideMarketRelayAsync(
                port, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromSeconds(2), CancellationToken.None);

            Assert.Null(relayed);
            Assert.Contains(logs, l => l.Contains("已监听但 web 面未就绪"));
            Assert.DoesNotContain(logs, l => l.Contains("收养"));
        }
        finally
        {
            listener.Stop();
            foreach (System.Diagnostics.Process p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.Dispose();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 假进程恰好已退出/无权限/平台不支持树杀：清理目标已达成
                }
            }

            Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>
    /// 接线面回归（R2 评审 Suggestion 采用，gated）：真实 RestartAsync 入口走接力分支——同一 host 实例
    /// 内（进程内重启：_port 已就位、supervisedStart 非空）在管 dsh 退出后，假 helper/续任者接管首选端口
    /// → 接力分支直接收养续任者（同端口裸 origin），绝不 spawn 竞争 dsh（.dsh-pid 记录被收养 pid+token
    /// 覆盖即为证）。
    /// </summary>
    [Fact]
    public async Task RestartAsync_PlantedRelayTakesOver_AdoptsSuccessorWithoutCompetingSpawn_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            return; // gated：需要真实 dsh 与真实 spawn 序
        }

        string home = Path.Combine(Path.GetTempPath(), "dsh-relay-e2e-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");
        var processes = new List<System.Diagnostics.Process>();
        try
        {
            Assert.True(DesktopProfileBootstrap.EnsureProfile(home));

            // 同一 host 实例的进程内重启：第一次 StartAsync 建立端口与 supervisedStart，
            // Stop 后栽假接力，RestartAsync 走接力分支（这是监督器实际走的路径）。
            var logs = new List<string>();
            using var host = new HarnessRuntimeHost(logs.Add);
            Uri? first;
            try
            {
                first = await host.StartAsync(TimeSpan.FromSeconds(30));
            }
            catch (Win32Exception)
            {
                return; // PATH 没有 dsh——跳过
            }

            Assert.NotNull(first);
            host.Stop(); // 模拟市场 helper 对在管 dsh 的 SIGTERM；监督器随后调 RestartAsync

            // 栽假接力：helper（带血统 env 的 marker cmdline）+ 续任者（服务端形状）接管首选端口，
            // 且该端口要按 HTTP 应答——就绪判据是 web 面可服务，不是端口可连（ADR relay-web-readiness）
            int port = first!.Port;
            using var responder = new CancellationTokenSource();
            (TcpListener relayListener, Task serving) = LoopbackHttpResponder.Start(
                port,
                LoopbackHttpResponder.Response("HTTP/1.1 401 Unauthorized", "dsh web authentication required; probe"),
                responder.Token);
            try
            {
                StartFakeRelayProcesses(home, "relay-e2e-token", processes);

                if (!OperatingSystem.IsLinux())
                {
                    return; // 血统枚举依赖 /proc；其他平台本测试无意义
                }

                Uri? restarted = await host.RestartAsync(TimeSpan.FromSeconds(60));
                host.Stop();

                Assert.NotNull(restarted);
                Assert.Equal(port, restarted!.Port);
                Assert.Contains(logs, l => l.Contains("市场接力续任者已接管首选端口"));
                Assert.Contains(logs, l => l.Contains("收养市场接力的续任者"));
                // 绝不 spawn 竞争：在管记录必须指向收养 token，而非新壳 spawn 的实例

                (int pid, string token)? record = OrphanDshReaper.ReadSpawnRecord(
                    HarnessRuntimeHost.ResolvePidFilePath());
                Assert.True(record is not null);
                Assert.Equal("relay-e2e-token", record!.Value.token);
            }
            finally
            {
                responder.Cancel();
                await serving;
                relayListener.Stop();
            }
        }
        finally
        {
            foreach (System.Diagnostics.Process p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.Dispose();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 假进程恰好已退出/无权限/平台不支持树杀：清理目标已达成
                }
            }

            Environment.SetEnvironmentVariable(HarnessRuntimeHost.HomeOverrideEnv, null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>起一对假接力进程：helper（cmdline 带上游重启 marker）+ 服务端形状续任者，均带血统 env。</summary>
    /// <param name="home">血统 DSH_HOME 取值（与探针枚举的判据对齐）。</param>
    /// <param name="token">血统 token（调用方按断言需要指定）。</param>
    /// <param name="processes">收尾列表：调用方负责 Kill 并 Dispose。</param>
    /// <remarks>cmdline 形状即判据：helper 携带 marker、服务端只认 <c>--profile &lt;name&gt;</c> 且绝不含
    /// helper 特征（marker / node -e / restart 字样），否则被先判为 helper。脚本保持多命令循环而非
    /// 「末命令 exec 优化」（dash 会把 -c 的最后一条命令 exec 替换进程映像，sh 本人退出、cmdline 丢
    /// 形状）：sh 必须活着意味着 cmdline 证据一直在场。</remarks>
    private static void StartFakeRelayProcesses(string home, string token, List<System.Diagnostics.Process> processes)
    {
        string alive = "while :; do sleep 1; done";
        string helperScript = "dsh-market-restart-probe=1 " + alive;
        string successorScript = $"dsh --profile {HarnessRuntimeHost.DesktopProfileName} --not-real >/dev/null 2>&1; " + alive;
        foreach (string script in new[] { helperScript, successorScript })
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh", $"-c \"{script}\"")
            {
                UseShellExecute = false,
            };
            psi.Environment[RuntimeLineage.TokenEnv] = token;
            psi.Environment[HarnessRuntimeHost.EcosystemHomeEnv] = home;
            processes.Add(System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动接力假进程"));
        }
    }
}
