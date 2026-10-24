using DeepSeek.Harness.Desktop.Core.Platform;
using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>
/// WebView2 启动前置（ADR avalonia-g4-webview2-preflight）：真值表固定三件事——可用即放行且
/// 不弹框；缺失才提示一次，且无论用户选什么都不放行（窗口无法渲染）；探测/提示/浏览器任一
/// 通道故障不得把进程带崩，也不得把「缺失」误判成可启动。
/// </summary>
public class WebView2StartupGuardTests
{
    /// <summary>探测可用：直接放行，不弹框、不开浏览器、不记退出日志。</summary>
    [Fact]
    public void EnsureAvailable_ProbeAvailable_SkipsDialog()
    {
        var harness = new Harness { Available = true };

        bool canStart = harness.Run();

        Assert.True(canStart);
        Assert.Empty(harness.Dialogs);
        Assert.Empty(harness.Browser.Opened);
        Assert.Empty(harness.Log);
    }

    /// <summary>缺失且用户选「是」：中文文案入框、打开下载页，仍不放行（返回 false）。</summary>
    [Fact]
    public void EnsureAvailable_MissingAndUserAgrees_OpensDownloadPageAndStops()
    {
        var harness = new Harness { Available = false, Answer = true };

        bool canStart = harness.Run();

        Assert.False(canStart);
        (string title, string message) = Assert.Single(harness.Dialogs);
        Assert.Equal(UiCopy.WebView2MissingTitle(english: false), title);
        Assert.Equal(UiCopy.WebView2MissingBody(english: false), message);
        Assert.Equal(WebView2StartupGuard.DownloadPageUrl, Assert.Single(harness.Browser.Opened));
        Assert.Contains(harness.Log, line => line.Contains("缺少 WebView2 运行时", StringComparison.Ordinal));
    }

    /// <summary>缺失且用户选「否」：不开浏览器，但同样不放行（窗口无法渲染）。</summary>
    [Fact]
    public void EnsureAvailable_MissingAndUserDeclines_DoesNotOpenBrowserAndStops()
    {
        var harness = new Harness { Available = false, Answer = false };

        bool canStart = harness.Run();

        Assert.False(canStart);
        Assert.Empty(harness.Browser.Opened);
        Assert.Single(harness.Dialogs);
    }

    /// <summary>英文 locale 走英文文案（标题与正文同源词典）。</summary>
    [Fact]
    public void EnsureAvailable_EnglishLocale_UsesEnglishCopy()
    {
        var harness = new Harness { Available = false, Answer = false, Locale = "en-US" };

        bool canStart = harness.Run();

        Assert.False(canStart);
        (string title, string message) = Assert.Single(harness.Dialogs);
        Assert.Equal(UiCopy.WebView2MissingTitle(english: true), title);
        Assert.Equal(UiCopy.WebView2MissingBody(english: true), message);
        Assert.DoesNotContain("缺少 WebView2", message, StringComparison.Ordinal);
    }

    /// <summary>探测自身抛错：fail-open 继续启动（探测缺陷不能变成启动阻断），并留痕。</summary>
    [Fact]
    public void EnsureAvailable_ProbeThrows_FailsOpen()
    {
        var harness = new Harness { ProbeFailure = new InvalidOperationException("registry boom") };

        bool canStart = harness.Run();

        Assert.True(canStart);
        Assert.Empty(harness.Dialogs);
        Assert.Contains(harness.Log, line => line.Contains("探测失败，按已安装处理", StringComparison.Ordinal));
    }

    /// <summary>提示通道故障：不能当成可启动（窗口仍无法渲染），留痕后退出。</summary>
    [Fact]
    public void EnsureAvailable_DialogThrows_StopsWithoutCrash()
    {
        var harness = new Harness { Available = false, DialogFailure = new InvalidOperationException("dialog boom") };

        bool canStart = harness.Run();

        Assert.False(canStart);
        Assert.Empty(harness.Browser.Opened);
        Assert.Contains(harness.Log, line => line.Contains("缺失提示无法展示", StringComparison.Ordinal));
    }

    /// <summary>「是」但浏览器返回未打开（不抛）：留痕，不改变退出结论。</summary>
    [Fact]
    public void EnsureAvailable_BrowserNotOpened_LogsFailure()
    {
        var harness = new Harness(browserOpens: false) { Available = false, Answer = true };

        bool canStart = harness.Run();

        Assert.False(canStart);
        Assert.Contains(harness.Log, line => line.Contains("系统浏览器未启动", StringComparison.Ordinal));
    }

    /// <summary>浏览器打开失败：只留痕，不改变「仍须退出」的结论。</summary>
    [Fact]
    public void EnsureAvailable_BrowserThrows_StopsWithoutCrash()
    {
        var harness = new Harness(browserThrows: true) { Available = false, Answer = true };

        bool canStart = harness.Run();

        Assert.False(canStart);
        Assert.Contains(harness.Log, line => line.Contains("打开 WebView2 下载页失败", StringComparison.Ordinal));
    }

    /// <summary>被测前置与可观测面的一次性组装。</summary>
    private sealed class Harness
    {
        /// <summary>创建组装。</summary>
        /// <param name="browserThrows">打开浏览器时抛异常（模拟系统浏览器不可用）。</param>
        /// <param name="browserOpens">浏览器返回是否已打开（false = 静默失败）。</param>
        public Harness(bool browserThrows = false, bool browserOpens = true) =>
            Browser = new FakeSystemBrowser(result: browserOpens, throwOnOpen: browserThrows);

        /// <summary>假运行时探测结果（false = 缺失）。</summary>
        public bool Available { get; init; }

        /// <summary>非 null 时探测抛该异常。</summary>
        public Exception? ProbeFailure { get; init; }

        /// <summary>模态返回值（true = 用户选「是」）。</summary>
        public bool Answer { get; init; }

        /// <summary>非 null 时模态抛该异常。</summary>
        public Exception? DialogFailure { get; init; }

        /// <summary>UI 语言（缺省中文；<c>en-US</c> 走英文分支）。</summary>
        public string? Locale
        {
            init
            {
                if (value is not null)
                {
                    LocaleState.Set(value);
                }
            }
        }

        /// <summary>假系统浏览器。</summary>
        public FakeSystemBrowser Browser { get; }

        /// <summary>弹出的模态（标题、正文）。</summary>
        public List<(string Title, string Message)> Dialogs { get; } = [];

        /// <summary>日志收集器。</summary>
        public List<string> Log { get; } = [];

        private UiLocale LocaleState { get; } = new();

        /// <summary>跑一次前置判定。</summary>
        /// <returns>是否可继续启动。</returns>
        public bool Run() => WebView2StartupGuard.EnsureAvailable(
            new FakeProbe(Available, ProbeFailure),
            Browser,
            LocaleState,
            (title, message) =>
            {
                if (DialogFailure is not null)
                {
                    throw DialogFailure;
                }

                Dialogs.Add((title, message));
                return Answer;
            },
            Log.Add);

        /// <summary>运行时探测替身。</summary>
        /// <param name="available">探测结果。</param>
        /// <param name="failure">非 null 时抛该异常。</param>
        private sealed class FakeProbe(bool available, Exception? failure) : IWebViewRuntimeProbe
        {
            /// <inheritdoc />
            public bool IsAvailable() => failure is null ? available : throw failure;
        }
    }
}
