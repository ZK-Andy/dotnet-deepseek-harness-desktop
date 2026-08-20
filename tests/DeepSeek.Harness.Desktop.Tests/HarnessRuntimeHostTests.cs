using System.ComponentModel;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>HarnessRuntimeHost 集成冒烟：真实 spawn dsh → 解析 URL。门控：设 DSH_TEST_E2E=1 且环境里有 dsh 才断言，否则自跳过。</summary>
public class HarnessRuntimeHostTests
{
    [Fact]
    public void BuildDshWebArgs_IncludesNoOpen_SoShellDoesNotHandOffToOsBrowser()
    {
        // rc.8+ 的 dsh web 默认 openBrowser=true，会把 URL 交给 OS 默认浏览器；
        // 桌面壳自渲染内嵌 WebView，必须传 --no-open 避免与桌面窗口重复弹出。
        var args = HarnessRuntimeHost.BuildDshWebArgs(0);
        Assert.Contains("--no-open", args);
        Assert.Equal("web", args[1]);
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
}
