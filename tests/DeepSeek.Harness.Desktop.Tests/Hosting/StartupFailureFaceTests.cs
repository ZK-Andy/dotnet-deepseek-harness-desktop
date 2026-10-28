using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>
/// 启动失败面（ADR 2026-10-23-avalonia-failure-dialog）：正文口径与破坏性动作的许可边界。
/// 真值表只钉三件事——文案取启动失败分支（不说「正在自动重启」）、尾部进正文、
/// 只有显式 true 才退出；模态故障与取消都不得变成退出许可。
/// </summary>
public class StartupFailureFaceTests
{
    /// <summary>用户选「退出应用」：正文＝启动失败原因 + stderr 尾部 + 启动失败动作说明，
    /// 且不含崩溃恢复的「自动重启」口径。</summary>
    [Fact]
    public async Task ShowAsync_QuitApproved_RunsQuitActionWithStartupCopy()
    {
        var harness = new Harness { Answer = true };

        await harness.CreateFace().ShowAsync(CancellationToken.None);

        Assert.Equal(1, harness.QuitCalls);
        (string title, string message) = Assert.Single(harness.Dialog.Shown);
        Assert.Equal(UiCopy.StartupFailureTitle(lang: UiLanguage.Zh), title);
        Assert.StartsWith(UiCopy.StartupFailureReason(lang: UiLanguage.Zh), message, StringComparison.Ordinal);
        Assert.Contains("tail-line", message);
        Assert.Contains(UiCopy.StartupFailureActions(lang: UiLanguage.Zh), message);
        Assert.DoesNotContain(UiCopy.RecoveryDialogActions(lang: UiLanguage.Zh), message);
    }

    /// <summary>留在窗口：不做任何破坏性动作。</summary>
    [Fact]
    public async Task ShowAsync_QuitDeclined_DoesNotQuit()
    {
        var harness = new Harness { Answer = false };

        await harness.CreateFace().ShowAsync(CancellationToken.None);

        Assert.Equal(0, harness.QuitCalls);
    }

    /// <summary>模态通道故障：留痕但不退出（通道故障不等于用户授权退出）。</summary>
    [Fact]
    public async Task ShowAsync_DialogFails_LogsAndDoesNotQuit()
    {
        var harness = new Harness();
        harness.Dialog.Failure = new InvalidOperationException("dialog boom");

        await harness.CreateFace().ShowAsync(CancellationToken.None);

        Assert.Equal(0, harness.QuitCalls);
        Assert.Contains(harness.Log, line => line.Contains("启动失败面承载失败", StringComparison.Ordinal));
    }

    /// <summary>等待被取消（退出路径）：不退出、不外抛。</summary>
    [Fact]
    public async Task ShowAsync_Cancelled_DoesNotQuit()
    {
        var harness = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await harness.CreateFace().ShowAsync(cts.Token);

        Assert.Equal(0, harness.QuitCalls);
    }

    /// <summary>英文 locale 走英文分支（标题/正文/动作同源词典）。</summary>
    [Fact]
    public async Task ShowAsync_EnglishLocale_UsesEnglishCopy()
    {
        var harness = new Harness(locale: "en-US") { Answer = true };

        await harness.CreateFace().ShowAsync(CancellationToken.None);

        (string title, string message) = Assert.Single(harness.Dialog.Shown);
        Assert.Equal(UiCopy.StartupFailureTitle(lang: UiLanguage.En), title);
        Assert.Contains(UiCopy.StartupFailureReason(lang: UiLanguage.En), message);
        Assert.DoesNotContain("运行时未能启动", message);
    }

    /// <summary>被测用例与可观测面的一次性组装。</summary>
    private sealed class Harness
    {
        /// <summary>创建组装；<paramref name="locale"/> 非 null 即切到该 UI 语言。</summary>
        /// <param name="locale">UI 语言（null = 默认中文）。</param>
        public Harness(string? locale = null)
        {
            Locale = new UiLocale();
            if (locale is not null)
            {
                Locale.Set(locale);
            }
        }

        /// <summary>模态返回值（true = 退出）。</summary>
        public bool Answer
        {
            get => Dialog.Answer;
            set => Dialog.Answer = value;
        }

        /// <summary>假失败面模态。</summary>
        public FakeFailureDialog Dialog { get; } = new();

        /// <summary>日志收集器。</summary>
        public List<string> Log { get; } = [];

        /// <summary>退出动作调用次数。</summary>
        public int QuitCalls { get; private set; }

        /// <summary>构造被测用例。</summary>
        /// <returns>启动失败面。</returns>
        public StartupFailureFace CreateFace() => new(
            Dialog,
            () => new[] { "tail-line" },
            Locale,
            () => QuitCalls++,
            Log.Add);

        private UiLocale Locale { get; }
    }
}
