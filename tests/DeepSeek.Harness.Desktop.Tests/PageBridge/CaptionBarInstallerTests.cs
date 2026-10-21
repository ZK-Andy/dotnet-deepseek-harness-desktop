using DeepSeek.Harness.Desktop.Tests.Fakes;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>自绘顶栏注入用例（ADR frameless-uniform-caption-bar + architecture/2026-10-21-window-script-target-port）：
/// 三路注入的调用序与幂等闩——接线即注入、每页加载注册、导航到达后重注入且只补注册一次。
/// 窗口面经脚本求值端口、webview 经惰性提供者进来，故三路都可离线驱动。</summary>
public class CaptionBarInstallerTests
{
    /// <summary>Install 即触发两路：当窗注入 + 每页加载注册；送进去的是同一份按当前参数构建的脚本。</summary>
    [Fact]
    public async Task Install_InjectsAndRegistersOnce()
    {
        using var harness = new Harness();

        await harness.Installer.InstallAsync();

        Assert.Equal(harness.ExpectedScript, harness.Target.LastScript);
        Assert.Equal(harness.ExpectedScript, harness.WebView.LastScript);
    }

    /// <summary>导航到达：重注入一次（顶栏 DOM 随整页导航销毁），并补注册一次（首次到达的一次性闩）。</summary>
    [Fact]
    public async Task Navigation_ReinjectsAndReregistersOnce()
    {
        using var harness = new Harness();
        await harness.Installer.InstallAsync();

        harness.Navigate();
        await WaitUntilAsync(() =>
            harness.Target.Scripts.Count >= 2 && harness.Target.LastScript is not null && harness.WebView.InjectCalls >= 2);

        Assert.Equal(2, harness.WebView.InjectCalls);
    }

    /// <summary>第二次及以后导航只重注入、不再新增注册（闩已消费，避免每次导航都加一条引擎注册）。</summary>
    [Fact]
    public async Task SecondNavigation_ReinjectsWithoutNewRegistration()
    {
        using var harness = new Harness();
        await harness.Installer.InstallAsync();
        harness.Navigate();
        await WaitUntilAsync(() =>
            harness.Target.Scripts.Count >= 2 && harness.Target.LastScript is not null && harness.WebView.InjectCalls >= 2);

        harness.Navigate();
        await WaitUntilAsync(() => harness.Target.Scripts.Count >= 3);

        Assert.Equal(2, harness.WebView.InjectCalls);
    }

    /// <summary>监督器令牌已取消（退出路径）：两路都不发起（重试单点在进入循环前检查取消）。</summary>
    [Fact]
    public async Task CancelledSupervision_SkipsBothPaths()
    {
        using var harness = new Harness();
#pragma warning disable CA1849 // 同步取消是测试前置条件，不涉 I/O
        harness.Cts.Cancel();
#pragma warning restore CA1849

        // 两路任务被 await 完，故「未发生」是确定结论而非定时窗口内的观察。
        await harness.Installer.InstallAsync();

        Assert.Empty(harness.Target.Scripts);
        Assert.Equal(0, harness.WebView.InjectCalls);
    }

    /// <summary>有界轮询等待（注入/注册走 Task.Run 且无返回句柄）：到点未满足即断言失败。</summary>
    /// <param name="condition">等待条件。</param>
    /// <param name="timeoutMilliseconds">上限（毫秒）。</param>
    /// <returns>条件满足即完成的任务。</returns>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        for (int waited = 0; waited < timeoutMilliseconds; waited += 10)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "等待注入/注册条件超时");
    }

    /// <summary>被测用例与可观测面的一次性组装。</summary>
    private sealed class Harness : IDisposable
    {
        /// <summary>按短节拍配置组装用例（重试节拍 1ms，避免用例等真实预算）。</summary>
        public Harness()
        {
            var pump = new PagePump(new RuntimeTimeouts
            {
                PushMaxAttempts = 3,
                PushRetryDelayMilliseconds = 1,
                CaptionBarRegisterSeconds = 1,
            });
            Installer = new CaptionBarInstaller(Target, WebView, Callbacks, Options, Locale, Cts, pump);
        }

        /// <summary>假脚本求值端口（注入面）。</summary>
        public FakeWindowScriptTarget Target { get; } = new();

        /// <summary>假 webview（每页加载注册面）。</summary>
        public FakeWebView WebView { get; } = new();

        /// <summary>真实导航回调集（用空浏览器端口构造，导航到达钩子的真实分发路径）。</summary>
        public RynNavigationCallbacks Callbacks { get; } = new(new FakeSystemBrowser());

        /// <summary>标题栏参数（非默认高度，证明选项确实进了脚本）。</summary>
        public CaptionBarOptions Options { get; } = new() { HeightPx = 44 };

        /// <summary>UI 语言单点（默认中文）。</summary>
        public UiLocale Locale { get; } = new();

        /// <summary>监督器取消令牌源（寿命 = 本次 Run 的替身）。</summary>
        public CancellationTokenSource Cts { get; } = new();

        /// <summary>被测用例。</summary>
        public CaptionBarInstaller Installer { get; }

        /// <summary>期望脚本：同一判据（参数 + 语言 + 平台闸）构建。</summary>
        public string ExpectedScript => CaptionBar.Build(Options.HeightPx, Locale, MacNativeChrome.IsEnabled);

        /// <summary>模拟一次导航到达（真实回调分发：单槽 + 常驻钩子）。</summary>
        public void Navigate() =>
            Callbacks.OnWebViewNavigated(new WebViewNavigatedContext(new Uri("http://127.0.0.1:41449/")));

        /// <inheritdoc />
        public void Dispose() => Cts.Dispose();
    }
}
