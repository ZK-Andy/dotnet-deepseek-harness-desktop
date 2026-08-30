using System.Text;
using DeepSeek.Harness.Desktop.Services;
using DeepSeek.Harness.Desktop.Services.Tray;
using DeepSeek.Harness.Desktop.Services.Update;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>托盘菜单构造：条目集合随自更新栈有无变化，分隔线与退出恒在。</summary>
public class TrayMenuBuildTests
{
    [Fact]
    public void BuildItems_WithUpdateStack_ContainsAllEntries()
    {
        List<TrayMenuItem> items = TrayMenuActions.BuildItems(includeUpdateItem: true);

        Assert.Equal(4, items.Count);
        Assert.Equal(TrayMenuActions.ShowItemId, items[0].Id);
        Assert.Equal("显示主窗", items[0].Label);
        Assert.Equal(TrayMenuActions.CheckUpdateItemId, items[1].Id);
        Assert.Equal("检查更新", items[1].Label);
        Assert.True(items[2].Separator);
        Assert.Equal(TrayMenuActions.QuitItemId, items[3].Id);
        Assert.Equal("退出", items[3].Label);
    }

    [Fact]
    public void BuildItems_WithoutUpdateStack_OmitsCheckEntry_KeepsSeparatorAndQuit()
    {
        List<TrayMenuItem> items = TrayMenuActions.BuildItems(includeUpdateItem: false);

        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, i => i.Id == TrayMenuActions.CheckUpdateItemId);
        Assert.True(items[1].Separator);
        Assert.Equal(TrayMenuActions.QuitItemId, items[2].Id);
    }

    [Fact]
    public void BuildItems_LocalizesEnglish_KeepsChineseDefault()
    {
        // 托盘双语（ADR host-ui-locale）：en 出英文，zh/缺省出中文（对齐 dsh 兜底方向）
        var en = new UiLocale();
        en.Set("en");
        List<TrayMenuItem> english = TrayMenuActions.BuildItems(includeUpdateItem: true, en);
        Assert.Equal("Show Main Window", english[0].Label);
        Assert.Equal("Check for Updates", english[1].Label);
        Assert.Equal("Quit", english[3].Label);

        var zh = new UiLocale();
        zh.Set("zh-CN");
        List<TrayMenuItem> chinese = TrayMenuActions.BuildItems(includeUpdateItem: true, zh);
        Assert.Equal("显示主窗", chinese[0].Label);
        Assert.Equal("检查更新", chinese[1].Label);
        Assert.Equal("退出", chinese[3].Label);

        var other = new UiLocale();
        other.Set("fr");
        Assert.Equal("退出", TrayMenuActions.BuildItems(true, other)[3].Label);
        Assert.Equal("退出", TrayMenuActions.BuildItems(true)[3].Label);
    }
}

/// <summary>托盘事件解析：事件名/条目 id 到宿主动作的映射，坏载荷一律忽略。</summary>
public class TrayActionResolveTests
{
    [Theory]
    [InlineData(TrayMenuActions.ShowItemId, TrayAction.ShowMainWindow)]
    [InlineData(TrayMenuActions.CheckUpdateItemId, TrayAction.CheckUpdate)]
    [InlineData(TrayMenuActions.QuitItemId, TrayAction.Quit)]
    public void MenuItemClicked_KnownIds_MapToActions(string id, TrayAction expected)
    {
        Assert.Equal(expected, TrayMenuActions.TryResolve(TrayMenuActions.MenuItemClickedEvent, id));
    }

    [Fact]
    public void IconClicked_MapsToShowMainWindow_RegardlessOfPayload()
    {
        Assert.Equal(TrayAction.ShowMainWindow, TrayMenuActions.TryResolve(TrayMenuActions.IconClickedEvent, null));
        Assert.Equal(TrayAction.ShowMainWindow, TrayMenuActions.TryResolve(TrayMenuActions.IconClickedEvent, "whatever"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("window.closeCancelled")]
    public void NonTrayEvents_ReturnNull(string? eventName)
    {
        Assert.Null(TrayMenuActions.TryResolve(eventName, TrayMenuActions.ShowItemId));
    }

    [Fact]
    public void MenuClicked_JsonQuotedPayload_DecodesThenMaps()
    {
        // 原生直传形态：载荷是 id 的 JSON 字符串（`"show"`），应解码后命中而非落 null
        Assert.Equal(TrayAction.ShowMainWindow, TrayMenuActions.TryResolve(TrayMenuActions.MenuItemClickedEvent, "\"show\""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-item")]
    [InlineData("{not-json")]
    [InlineData("\"unterminated")]
    public void MenuClicked_BadOrUnknownPayload_ReturnsNull(string? data)
    {
        Assert.Null(TrayMenuActions.TryResolve(TrayMenuActions.MenuItemClickedEvent, data));
    }
}

/// <summary>关窗闸门：默认拦截（hide-to-tray），批准后放行且幂等。</summary>
public class CloseGateTests
{
    [Fact]
    public void Default_InterceptsClose_UntilApproved()
    {
        var gate = new CloseGate();
        Assert.True(gate.ShouldCancelClose);
    }

    [Fact]
    public void ApproveExit_ReleasesGate_Idempotently()
    {
        var gate = new CloseGate();
        gate.ApproveExit();
        gate.ApproveExit();
        Assert.False(gate.ShouldCancelClose);
    }
}

/// <summary>
/// 托盘命令路由行为契约。核心是退出路径的**顺序**：「先 ApproveExit 再 Close」——
/// 调换即静默复发「退出变隐藏」，故以记序 fake 钉住 Close 调用瞬间闸门必须已放行。
/// </summary>
public class DesktopTrayCommandRouterTests
{
    private static (DesktopTrayCommandRouter Router, List<string> Calls) MakeRouter(
        CloseGate gate,
        UpdateStateMachine? machine = null,
        List<string>? logs = null)
    {
        var calls = new List<string>();
        var router = new DesktopTrayCommandRouter(
            showWindow: () => { calls.Add("show"); return Task.CompletedTask; },
            closeWindow: () =>
            {
                // 记录 Close 发生时闸门状态：顺序契约的断言点
                calls.Add(gate.ShouldCancelClose ? "close:locked" : "close:released");
            },
            closeGate: gate,
            updateMachine: machine,
            log: logs is null ? null : logs.Add);
        return (router, calls);
    }

    private static ValueTask<string> Route(DesktopTrayCommandRouter router, string json)
    {
        return router.RouteAsync(
            DesktopTrayCommandRouter.CommandName,
            Encoding.UTF8.GetBytes(json),
            null!,
            CancellationToken.None);
    }

    [Fact]
    public async Task Quit_ApprovesGateBeforeClosing_OrderContract()
    {
        var gate = new CloseGate();
        (DesktopTrayCommandRouter? router, List<string>? calls) = MakeRouter(gate);

        string frame = await Route(router, """{"event":"tray.menuItemClicked","data":"quit"}""");

        Assert.Equal(new[] { "close:released" }, calls);
        Assert.False(gate.ShouldCancelClose);
        Assert.Equal("{}", frame);
    }

    [Fact]
    public async Task ShowMainWindow_TriggersShow_Only()
    {
        (DesktopTrayCommandRouter? router, List<string>? calls) = MakeRouter(new CloseGate());

        await Route(router, """{"event":"tray.clicked"}""");

        Assert.Equal(new[] { "show" }, calls);
    }

    [Fact]
    public async Task ShowMainWindow_Success_Logged()
    {
        // 成功路径留痕契约：托盘事件到达性排查此前只有失败分支可查
        var logs = new List<string>();
        (DesktopTrayCommandRouter? router, List<string> _) = MakeRouter(new CloseGate(), logs: logs);

        await Route(router, """{"event":"tray.clicked"}""");

        Assert.Contains(logs, l => l.Contains("显示主窗"));
    }

    [Fact]
    public async Task CheckUpdate_WithoutUpdateStack_NoSideEffect_AcceptedFrame()
    {
        (DesktopTrayCommandRouter? router, List<string>? calls) = MakeRouter(new CloseGate());

        string frame = await Route(router, """{"event":"tray.menuItemClicked","data":"check-update"}""");

        Assert.Empty(calls);
        Assert.Equal("{}", frame);
    }

    [Theory]
    [InlineData("""{"event":"some.otherEvent","data":"quit"}""")]
    [InlineData("""{"event":"tray.menuItemClicked","data":"unknown-item"}""")]
    [InlineData("""{"event":123}""")]
    [InlineData("""not-json""")]
    public async Task IgnoredEvents_ReturnNullFrame_NoWindowActions(string body)
    {
        (DesktopTrayCommandRouter? router, List<string>? calls) = MakeRouter(new CloseGate());

        string frame = await Route(router, body);

        Assert.Empty(calls);
        Assert.Equal("null", frame);
    }

    [Fact]
    public async Task TrayEvent_Arrival_AlwaysLogged()
    {
        // 事件到达性留痕契约（本轮日志收口）：任何托盘事件都必须在日志里留到达痕，
        // 「事件到没到」从此与「到了被忽略/处理卡死」可区分
        var logs = new List<string>();
        (DesktopTrayCommandRouter? router, List<string> _) = MakeRouter(new CloseGate(), logs: logs);

        await Route(router, """{"event":"tray.menuItemClicked","data":"unknown-item"}""");

        Assert.Contains(logs, l => l.Contains("事件到达") && l.Contains("tray.menuItemClicked"));
    }

    [Fact]
    public async Task IgnoredEvent_ArrivalAndIgnore_BothLogged()
    {
        // 到达 + 忽略双痕：未知条目不再静默——忽略原因可见（排查盲区最后一环）
        var logs = new List<string>();
        (DesktopTrayCommandRouter? router, List<string> _) = MakeRouter(new CloseGate(), logs: logs);

        await Route(router, """{"event":"tray.menuItemClicked","data":"bogus-item"}""");

        Assert.Contains(logs, l => l.Contains("事件到达"));
        Assert.Contains(logs, l => l.Contains("事件忽略") && l.Contains("bogus-item"));
    }

    [Fact]
    public async Task CheckUpdate_AcceptedAndStackStatus_Logged()
    {
        // 检查更新的受理与装载状态都要有痕：无状态机时打「栈未装载」，有栈时打「已受理」
        var logs = new List<string>();
        (DesktopTrayCommandRouter? router, List<string> _) = MakeRouter(new CloseGate(), logs: logs);

        await Route(router, """{"event":"tray.menuItemClicked","data":"check-update"}""");

        Assert.Contains(logs, l => l.Contains("检查更新") && l.Contains("已受理"));
        Assert.Contains(logs, l => l.Contains("自更新栈未装载"));
    }
}

/// <summary>托盘「检查更新」通知文案映射：结束态给结论，中间态不打扰。</summary>
public class TrayCheckFeedbackTests
{
    [Fact]
    public void UpToDate_PromptsAlreadyLatest()
    {
        string? message = TrayCheckFeedback.Message(new Services.Update.UpdateState(Services.Update.UpdateStatus.UpToDate, Current: "9.9.9"));

        Assert.Equal("已是最新版本", message);
    }

    [Fact]
    public void Ready_IncludesTargetVersion_AndInstallHint()
    {
        string? message = TrayCheckFeedback.Message(new Services.Update.UpdateState(Services.Update.UpdateStatus.Ready, Version: "1.2.3"));

        Assert.Contains("1.2.3", message);
        Assert.Contains("桌面设置", message);
    }

    [Fact]
    public void Error_IncludesReason()
    {
        string? message = TrayCheckFeedback.Message(new Services.Update.UpdateState(Services.Update.UpdateStatus.Error, Message: "网络不可达"));

        Assert.Contains("检查更新失败", message);
        Assert.Contains("网络不可达", message);
    }

    [Theory]
    [InlineData(Services.Update.UpdateStatus.Idle)]
    [InlineData(Services.Update.UpdateStatus.Checking)]
    [InlineData(Services.Update.UpdateStatus.Downloading)]
    [InlineData(Services.Update.UpdateStatus.Installing)]
    public void IntermediateStates_DoNotNotify(Services.Update.UpdateStatus status)
    {
        Assert.Null(TrayCheckFeedback.Message(new Services.Update.UpdateState(status)));
    }
}

/// <summary>托盘唤回的最大化动作判据：仅隐藏前采样为最大化才发出；未知与非最大化绝不动作。</summary>
public class TrayRecallMaximizeTests
{
    [Theory]
    [InlineData(1, true)]   // 隐藏前最大化 → 预置/兜底发出 SetMaximized(true)
    [InlineData(0, false)]  // 隐藏前非最大化 → 不动
    [InlineData(-1, false)] // 采样未知 → 绝不动（行为退回修复前）
    public void ShouldEnsure_MatchesContract(int maximizedAtHide, bool expected)
    {
        Assert.Equal(expected, TrayRecallMaximize.ShouldEnsure(maximizedAtHide));
    }
}
