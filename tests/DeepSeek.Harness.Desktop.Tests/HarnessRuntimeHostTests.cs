using System.ComponentModel;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>HarnessRuntimeHost 集成冒烟：真实 spawn dsh → 解析 URL。门控：设 DSH_TEST_E2E=1 且环境里有 dsh 才断言，否则自跳过。</summary>
/// <remarks>环境变量型测试与 <see cref="DeepSeek.Harness.Desktop.Tests.SharedHomeContractTests"/> 同集合串行——
/// 两者都改写进程级 DSH_HOME 覆盖变量，并行会互相污染（实测 flaky 教训）。</remarks>
[Collection("dsh-home-env")]
public class HarnessRuntimeHostTests
{
    [Fact]
    public void BuildDshWebArgs_IncludesNoOpen_SoShellDoesNotHandOffToOsBrowser()
    {
        // rc.8+ 的 dsh web 默认 openBrowser=true，会把 URL 交给 OS 默认浏览器；
        // 桌面壳自渲染内嵌 WebView，必须传 --no-open 避免与桌面窗口重复弹出。
        var args = HarnessRuntimeHost.BuildDshWebArgs(0);
        Assert.Contains("--no-open", args);
        Assert.Equal(HarnessRuntimeHost.DesktopProfileName, args[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildEnrichedPath_EmptyOrNullPath_BecomesLocalBinOnly(string? current)
    {
        Assert.Equal("/home/u/.local/bin", HarnessRuntimeHost.BuildEnrichedPath(current, "/home/u", ':'));
    }

    [Fact]
    public void BuildEnrichedPath_MissingLocalBin_AppendsAfterExisting()
    {
        var enriched = HarnessRuntimeHost.BuildEnrichedPath("/usr/local/bin:/usr/bin", "/home/u", ':');
        Assert.Equal("/usr/local/bin:/usr/bin:/home/u/.local/bin", enriched);
    }

    [Fact]
    public void BuildEnrichedPath_AlreadyPresent_Idempotent()
    {
        const string path = "/home/u/.local/bin:/usr/bin";
        Assert.Equal(path, HarnessRuntimeHost.BuildEnrichedPath(path, "/home/u", ':'));
    }

    [Fact]
    public void BuildEnrichedPath_SimilarPrefixSegment_DoesNotFalsePositive()
    {
        // /home/u/.local/bin-extra 不是 ~/.local/bin 本尊，不得据此判已含。
        var enriched = HarnessRuntimeHost.BuildEnrichedPath("/home/u/.local/bin-extra", "/home/u", ':');
        Assert.Equal("/home/u/.local/bin-extra:/home/u/.local/bin", enriched);
    }

    [Fact]
    public async Task StartAsync_ParsesRealDshWebUrl_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), "dsh-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");

        try
        {
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

    [Fact]
    public async Task RestartAsync_AfterChildKilled_YieldsNewUrl_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), "dsh-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");

        try
        {
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
            var exit = host.WaitForExitAsync();
            host.Stop(); // 模拟子进程被终止
            await exit.WaitAsync(TimeSpan.FromSeconds(5));

            var restarted = await host.RestartAsync(TimeSpan.FromSeconds(30));
            host.Stop();

            Assert.NotNull(restarted);
            // 稳定端口：重启后 URL 相同（origin 不变），Web UI 才能记住上一会话
            Assert.Equal(first, restarted);
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

    [Fact]
    public void PersistPort_ThenLoad_RoundTripsUnderDshHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void LegacyHomeRootFile_UsedWhenProfileFileMissing()
    {
        // 迁移回读：0.3.5 及之前把端口记忆写在 home 根；升级后首次启动应零感知沿用
        var home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "profiles", "desktop"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        try
        {
            File.WriteAllText(HarnessRuntimeHost.ResolveLegacyPortFilePath(), "46777");
            Assert.Equal(46777, HarnessRuntimeHost.TryLoadPersistedPort());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", null);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Persist_WritesProfilePathOnly_LegacyFileUntouched()
    {
        // 写入绝不回流旧位置：跨 profile 争抢不能借尸还魂
        var home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ProfileFile_MissingButLegacyCorrupt_ReturnsNull()
    {
        // 旧文件损坏同样按无记忆处理，不得抛出阻断启动
        var home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void TryLoadPersistedPort_CorruptFile_ReturnsNull()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-port-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "profiles", "desktop"));
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

    [Fact]
    public async Task StartAsync_PersistsPort_AcrossFreshInstances_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DSH_TEST_E2E") != "1")
        {
            // 未启用——保持绿色
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), "dsh-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", home);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "placeholder");

        try
        {
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
                var second = await secondHost.StartAsync(TimeSpan.FromSeconds(30));
                secondHost.Stop();
                Assert.NotNull(second);
                Assert.Equal(first, second);
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
}
