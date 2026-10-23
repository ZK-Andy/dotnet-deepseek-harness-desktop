using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.Update;

/// <summary>自更新结论反馈面（ADR avalonia-g3-update-surface）：Avalonia 托盘无通知面，托盘触发的检查
/// 结论落页面横幅 + 唤回主窗。契约要点：就绪复用就绪横幅 id（与协调器自身订阅去重）、已是最新/失败
/// 落检查横幅（同名替换）、中间态不打扰、文案随宿主 locale。</summary>
public class UpdateCheckPresenterTests
{
    private static readonly PagePump s_pump = new(new RuntimeTimeouts());

    private static (UpdateCheckPresenter Presenter, FakeWindowScriptTarget Target, List<int> Recalls) Create(UiLocale? locale = null)
    {
        var target = new FakeWindowScriptTarget();
        var recalls = new List<int>();
        var presenter = new UpdateCheckPresenter(
            target,
            s_pump,
            locale ?? new UiLocale(),
            () => recalls.Add(1),
            _ => { });
        return (presenter, target, recalls);
    }

    /// <summary>已是最新：先唤回主窗，再注入检查横幅（不是就绪横幅），文案取自托盘结论词典。</summary>
    [Fact]
    public async Task Present_UpToDate_RecallsWindowAndShowsCheckBanner()
    {
        var en = new UiLocale();
        en.Set("en");
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, List<int> recalls) = Create(en);
        target.EnqueueResult("ui");

        await presenter.PresentAsync(new UpdateState(UpdateStatus.UpToDate, Current: "1.0.0"), CancellationToken.None);

        Assert.Single(recalls);
        Assert.NotNull(target.LastScript);
        Assert.Contains($"var id='{DesktopBanner.UpdateCheckBannerId}'", target.LastScript);
        Assert.Contains("Already up to date", target.LastScript);
        // 确认「已是最新」使就绪横幅失效：先清掉它，免得两张结论并存
        Assert.Contains($"document.getElementById('{DesktopBanner.UpdateReadyBannerId}')", target.LastScript);
    }

    /// <summary>失败：横幅带原因与警示色（按钮取 state-warn token），失败原因经转义注入。</summary>
    [Fact]
    public async Task Present_Error_ShowsWarnBannerWithReason()
    {
        var en = new UiLocale();
        en.Set("en");
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, List<int> recalls) = Create(en);
        target.EnqueueResult("ui");

        await presenter.PresentAsync(
            new UpdateState(UpdateStatus.Error, Message: "HTTP 503"), CancellationToken.None);

        Assert.Single(recalls);
        Assert.NotNull(target.LastScript);
        Assert.Contains("Update check failed: HTTP 503", target.LastScript);
        Assert.Contains("var(--dsw-alias-state-warn-primary,#b45309)", target.LastScript);
        // 失败不清就绪横幅：网络抖动不该抹掉可操作的安装提示
        Assert.DoesNotContain($"document.getElementById('{DesktopBanner.UpdateReadyBannerId}')", target.LastScript);
    }

    /// <summary>就绪：复用就绪横幅 id（协调器自身的 ready 订阅同一 id，先到者胜），不另造第二张。</summary>
    [Fact]
    public async Task Present_Ready_ReusesReadyBannerId()
    {
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, List<int> recalls) = Create();
        target.EnqueueResult("ui");

        await presenter.PresentAsync(
            new UpdateState(UpdateStatus.Ready, Version: "1.2.3"), CancellationToken.None);

        Assert.Single(recalls);
        Assert.NotNull(target.LastScript);
        Assert.Contains("var id='dsh-desktop-update-ready-banner'", target.LastScript);
        Assert.Contains("1.2.3", target.LastScript);
        // 就绪结论清掉上次的检查结论（同名替换之外的另一半互斥）
        Assert.Contains($"document.getElementById('{DesktopBanner.UpdateCheckBannerId}')", target.LastScript);
    }

    /// <summary>中间态（检查中/下载中）：不唤回、不注入——过程噪音只在 host.log。</summary>
    [Theory]
    [InlineData(UpdateStatus.Checking)]
    [InlineData(UpdateStatus.Downloading)]
    [InlineData(UpdateStatus.Installing)]
    [InlineData(UpdateStatus.Idle)]
    public async Task Present_IntermediateState_DoesNotDisturb(UpdateStatus status)
    {
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, List<int> recalls) = Create();

        await presenter.PresentAsync(new UpdateState(status), CancellationToken.None);

        Assert.Empty(recalls);
        Assert.Empty(target.Scripts);
    }

    /// <summary>同名替换：结论横幅先移除旧横幅再注入，连续两次结论不同时是刷新而不是静默丢弃。</summary>
    [Fact]
    public async Task Present_ReplacesExistingCheckBanner()
    {
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, _) = Create();
        target.EnqueueResult("ui");

        await presenter.PresentAsync(new UpdateState(UpdateStatus.UpToDate), CancellationToken.None);

        Assert.NotNull(target.LastScript);
        Assert.Contains("var old=document.getElementById(id);if(old)old.remove();", target.LastScript);
    }

    /// <summary>托盘动作路径：后台跑一次检查，结束时交付结论（就绪/已是最新/失败三态之一）。</summary>
    [Fact]
    public async Task CheckInBackground_DeliversResultToBanner()
    {
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, List<int> recalls) = Create();
        target.EnqueueResult("ui");
        UpdateStateMachine machine = NewMachine(check: _ => Task.FromResult<ReleaseMeta?>(null));

        await presenter.CheckInBackgroundAsync(machine, static () => CancellationToken.None);

        Assert.Single(recalls);
        Assert.NotNull(target.LastScript);
        Assert.Contains($"var id='{DesktopBanner.UpdateCheckBannerId}'", target.LastScript);
    }

    /// <summary>取消已发生：不唤回、不注入（退出期不再把窗口从托盘拽出来）。</summary>
    [Fact]
    public async Task Present_CancelledToken_DoesNotRecallOrInject()
    {
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, List<int> recalls) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await presenter.PresentAsync(new UpdateState(UpdateStatus.UpToDate), cts.Token);

        Assert.Empty(recalls);
        Assert.Empty(target.Scripts);
    }

    /// <summary>监督器令牌源已随 ShellResources 释放：取 token 抛 ObjectDisposedException 被收口留痕，
    /// 不上抛成未观察任务异常、不注入半张横幅。</summary>
    [Fact]
    public async Task CheckInBackground_DisposedTokenSource_SwallowsAndDoesNotInject()
    {
        (UpdateCheckPresenter presenter, FakeWindowScriptTarget target, List<int> recalls) = Create();
        UpdateStateMachine machine = NewMachine(check: _ => Task.FromResult<ReleaseMeta?>(null));

        await presenter.CheckInBackgroundAsync(machine, static () => throw new ObjectDisposedException("supervisor-cts"));

        Assert.Empty(recalls);
        Assert.Empty(target.Scripts);
    }

    private static UpdateStateMachine NewMachine(UpdateStateMachine.CheckDelegate check) =>
        new(
            currentVersion: "1.0.0",
            check: check,
            download: (_, _) => Task.FromResult("/tmp/unused.pkg"),
            install: (_, _, _) => Task.CompletedTask,
            persistence: new InMemoryUpdatePersistence());
}
