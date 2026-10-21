using DeepSeek.Harness.Desktop.Core.Recovery;
using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.Recovery;

/// <summary>失败面承载适配器（ADR native-failure-surface + architecture/2026-10-21-window-script-target-port）：
/// 窗口面改经 <see cref="IWindowScriptTarget"/> 与「闸门 + 关窗动作」两端口后，原生/页内两条承载路径的
/// 顺序契约（先批准再关窗、破坏性动作只挂显式 true、失败只留日志）进入自动化回归。</summary>
public class RecoverySurfaceTests
{
    /// <summary>原生通道可用性只是对话框端口的投影（调用方据此决定回落）。</summary>
    [Fact]
    public void HasNativeChannel_MirrorsDialogPort()
    {
        var harness = new Harness();
        harness.Dialog.IsAvailable = false;
        Assert.False(harness.CreateSurface().HasNativeChannel);

        harness.Dialog.IsAvailable = true;
        Assert.True(harness.CreateSurface().HasNativeChannel);
    }

    /// <summary>用户选「退出应用」：先批准关窗闸门再关窗（hide-to-tray 下未批准的 Close 会吞成隐藏），
    /// 正文含失败原因与诊断包路径、标题走原生词典。</summary>
    [Fact]
    public async Task ShowNativeAsync_QuitApproved_ApprovesGateThenCloses()
    {
        var harness = new Harness();
        harness.Dialog.Answer = true;
        RecoverySurface surface = harness.CreateSurface(
            new FakeDiagnosticsExport(DiagnosticsExportOutcome.Success("/tmp/diag.zip")));

        await surface.ShowNativeAsync("运行时进程意外退出", CancellationToken.None);

        Assert.False(harness.Gate.ShouldCancelClose);
        Assert.Equal(1, harness.CloseCalls);
        // 顺序契约取「关窗瞬间」的闸门态：只在事后看最终态的话，把「先批准再关窗」两行对调也全绿。
        Assert.True(harness.ClosedWithGateReleased);
        (string title, string message) = Assert.Single(harness.Dialog.Shown);
        Assert.Equal(UiCopy.RecoveryDialogTitle(english: false), title);
        Assert.Contains("运行时进程意外退出", message);
        Assert.Contains("/tmp/diag.zip", message);
    }

    /// <summary>false 同时表示「用户否定」与「通道故障」，故不得触发任何破坏性动作（闸门不动、窗口不关）。</summary>
    [Fact]
    public async Task ShowNativeAsync_QuitDeclined_DoesNotExit()
    {
        var harness = new Harness();
        harness.Dialog.Answer = false;

        await harness.CreateSurface().ShowNativeAsync("原因", CancellationToken.None);

        Assert.True(harness.Gate.ShouldCancelClose);
        Assert.Equal(0, harness.CloseCalls);
    }

    /// <summary>诊断导出失败：正文取失败文案而不是空路径（失败面不得静默丢掉求助路径）。</summary>
    [Fact]
    public async Task ShowNativeAsync_DiagnosticsFailure_UsesFailureCopy()
    {
        var harness = new Harness();
        RecoverySurface surface = harness.CreateSurface(
            new FakeDiagnosticsExport(DiagnosticsExportOutcome.Failure("disk full")));

        await surface.ShowNativeAsync("原因", CancellationToken.None);

        (_, string message) = Assert.Single(harness.Dialog.Shown);
        Assert.Contains(UiCopy.RecoveryDialogExportFailed(english: false), message);
        Assert.DoesNotContain("disk full", message);
    }

    /// <summary>英文 locale 走英文分支（标题与导出提示同源词典）。</summary>
    [Fact]
    public async Task ShowNativeAsync_EnglishLocale_UsesEnglishCopy()
    {
        var harness = new Harness(locale: "en-US");
        RecoverySurface surface = harness.CreateSurface(
            new FakeDiagnosticsExport(DiagnosticsExportOutcome.Success("/tmp/diag.zip")));

        await surface.ShowNativeAsync("crash", CancellationToken.None);

        (string title, string message) = Assert.Single(harness.Dialog.Shown);
        Assert.Equal(UiCopy.RecoveryDialogTitle(english: true), title);
        Assert.Contains(UiCopy.RecoveryDialogExportPrefix(english: true), message);
    }

    /// <summary>关窗动作抛异常：只留日志不外抛（失败面已在退出路上，再抛会把异常丢给监督循环）。</summary>
    [Fact]
    public async Task ShowNativeAsync_CloseThrows_LogsAndSwallows()
    {
        var harness = new Harness { CloseThrows = true };
        harness.Dialog.Answer = true;

        await harness.CreateSurface().ShowNativeAsync("原因", CancellationToken.None);

        Assert.False(harness.Gate.ShouldCancelClose);
        Assert.Contains(harness.Log, line => line.Contains("失败面退出动作失败", StringComparison.Ordinal));
    }

    /// <summary>原生模态通道故障（假件抛异常）：异常向调用方传播（监督器据此回落页内面），
    /// 但闸门与窗口都不许动——通道故障不等于用户授权退出。</summary>
    [Fact]
    public async Task ShowNativeAsync_DialogFails_DoesNotExit()
    {
        var harness = new Harness();
        harness.Dialog.Failure = new InvalidOperationException("dialog boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.CreateSurface().ShowNativeAsync("原因", CancellationToken.None));

        Assert.True(harness.Gate.ShouldCancelClose);
        Assert.Equal(0, harness.CloseCalls);
    }

    /// <summary>页内回落面：把原因与语言分支交给脚本构建器（脚本内容面已由 RecoveryPageBuilderTests 钉住）。</summary>
    [Fact]
    public async Task ShowPageAsync_EvaluatesRecoveryScript()
    {
        var harness = new Harness();

        await harness.CreateSurface().ShowPageAsync("恢复原因");

        string script = Assert.Single(harness.Target.Scripts);
        Assert.Equal(RecoveryPageBuilder.BuildScript("恢复原因", [], english: false), script);
    }

    /// <summary>被测适配器与可观测面（日志/关窗计数/闸门）的一次性组装。</summary>
    private sealed class Harness
    {
        private readonly string? _locale;

        /// <summary>创建组装；<paramref name="locale"/> 非 null 即切到该 UI 语言。</summary>
        /// <param name="locale">UI 语言（null = 默认中文）。</param>
        public Harness(string? locale = null) => _locale = locale;

        /// <summary>假脚本求值端口（页内面靶点）。</summary>
        public FakeWindowScriptTarget Target { get; } = new();

        /// <summary>假原生模态端口。</summary>
        public FakeFailureDialog Dialog { get; } = new();

        /// <summary>真实关窗闸门（纯状态类）。</summary>
        public CloseGate Gate { get; } = new();

        /// <summary>UI 语言单点。</summary>
        public UiLocale Locale { get; } = new();

        /// <summary>日志行。</summary>
        public List<string> Log { get; } = [];

        /// <summary>关窗动作被调用次数。</summary>
        public int CloseCalls { get; private set; }

        /// <summary>关窗动作发生时闸门是否已放行（顺序契约的取证位）。</summary>
        public bool ClosedWithGateReleased { get; private set; }

        /// <summary>关窗动作是否抛异常（失败面据此只留日志）。</summary>
        public bool CloseThrows { get; init; }

        /// <summary>按当前组装造一个失败面适配器。</summary>
        /// <param name="diagnostics">诊断导出替身（缺省成功 + 假路径）。</param>
        /// <returns>被测适配器。</returns>
        public RecoverySurface CreateSurface(FakeDiagnosticsExport? diagnostics = null)
        {
            if (_locale is not null)
            {
                Locale.Set(_locale);
            }

            return new RecoverySurface(
                new HarnessRuntimeHost(Log.Add),
                Target,
                Dialog,
                diagnostics ?? new FakeDiagnosticsExport(),
                Locale,
                Gate,
                Close,
                Log.Add);
        }

        private void Close()
        {
            ClosedWithGateReleased = !Gate.ShouldCancelClose;
            CloseCalls++;
            if (CloseThrows)
            {
                throw new InvalidOperationException("close boom");
            }
        }
    }
}
