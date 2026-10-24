using DeepSeek.Harness.Desktop.Core.Ipc;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// <c>window.*</c> 命令路由（ADR 2026-10-23-avalonia-caption-bar）：页面侧顶栏脚本与宿主之间的
/// 六个窗口命令——参数帧解析（屏幕坐标/边/全屏）、非法参数回错误帧而不是 IPC 异常、
/// 未知命令走 <see cref="CommandNotFoundException"/>。平台互操作不在本层，故全部可 headless 钉住。
/// </summary>
public class WindowCommandRouterTests
{
    /// <summary>无参命令逐条落到动作面。</summary>
    [Theory]
    [InlineData(HostCommandIds.WindowToggleMaximize)]
    [InlineData(HostCommandIds.WindowMinimize)]
    [InlineData(HostCommandIds.WindowClose)]
    public async Task RouteAsync_SimpleCommands_Dispatch(string command)
    {
        var commands = new RecordingWindowCommands();
        var router = new WindowCommandRouter(commands);

        string result = await router.RouteAsync(command, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal("{}", result);
        Assert.Equal(command, Assert.Single(commands.Calls));
    }

    /// <summary>拖拽帧带屏幕坐标：页面坐标 → 屏幕坐标的折算在页面侧完成，宿主只消费。</summary>
    [Fact]
    public async Task RouteAsync_StartDrag_PassesScreenPoint()
    {
        var commands = new RecordingWindowCommands();
        var router = new WindowCommandRouter(commands);

        await router.RouteAsync(
            HostCommandIds.WindowStartDrag,
            Json("""{"x":120,"y":34}"""),
            CancellationToken.None);

        Assert.Equal("drag:120,34", Assert.Single(commands.Calls));
    }

    /// <summary>缩放帧的边名按 kebab 形态解析（与页面脚本的 <c>top-left</c> 同形）。</summary>
    [Theory]
    [InlineData("top-left", "TopLeft")]
    [InlineData("bottom", "Bottom")]
    [InlineData("right", "Right")]
    public async Task RouteAsync_StartResize_MapsEdge(string edge, string expected)
    {
        var commands = new RecordingWindowCommands();
        var router = new WindowCommandRouter(commands);

        await router.RouteAsync(
            HostCommandIds.WindowStartResize,
            Json($$"""{"edge":"{{edge}}","x":5,"y":7}"""),
            CancellationToken.None);

        Assert.Equal($"resize:{expected}:5,7", Assert.Single(commands.Calls));
    }

    /// <summary>非法/缺字段参数回错误帧（页面按 error 展示），动作面不收到调用。</summary>
    [Theory]
    [InlineData(HostCommandIds.WindowStartDrag, "not-json")]
    [InlineData(HostCommandIds.WindowStartDrag, """{"x":1}""")]
    [InlineData(HostCommandIds.WindowStartResize, """{"edge":"middle","x":1,"y":2}""")]
    [InlineData(HostCommandIds.WindowStartResize, """{"edge":"left"}""")]
    [InlineData(HostCommandIds.WindowSetFullscreen, """{"fullscreen":"yes"}""")]
    public async Task RouteAsync_InvalidArgs_ReturnsErrorFrame(string command, string args)
    {
        var commands = new RecordingWindowCommands();
        var router = new WindowCommandRouter(commands);

        string result = await router.RouteAsync(command, Json(args), CancellationToken.None);

        Assert.Contains("\"error\"", result, StringComparison.Ordinal);
        Assert.Empty(commands.Calls);
    }

    /// <summary>HiDPI 下页面发来的是小数坐标（clientX 可为 .5）：按四舍五入取整，不得回错误帧。</summary>
    [Fact]
    public async Task RouteAsync_StartDrag_DecimalCoordinatesAreRounded()
    {
        var commands = new RecordingWindowCommands();
        var router = new WindowCommandRouter(commands);

        string result = await router.RouteAsync(
            HostCommandIds.WindowStartDrag,
            Json("""{"x":120.6,"y":34.4}"""),
            CancellationToken.None);

        Assert.Equal("{}", result);
        Assert.Equal("drag:121,34", Assert.Single(commands.Calls));
    }

    /// <summary>全屏帧布尔值直通。</summary>
    [Fact]
    public async Task RouteAsync_SetFullscreen_PassesFlag()
    {
        var commands = new RecordingWindowCommands();
        var router = new WindowCommandRouter(commands);

        await router.RouteAsync(
            HostCommandIds.WindowSetFullscreen, Json("""{"fullscreen":true}"""), CancellationToken.None);
        await router.RouteAsync(
            HostCommandIds.WindowSetFullscreen, Json("""{"fullscreen":false}"""), CancellationToken.None);

        Assert.Equal(["fullscreen:true", "fullscreen:false"], commands.Calls);
    }

    /// <summary>只认本路由的六个命令；其余一律不接管（路由表按 <c>CanRoute</c> 顺序找家）。</summary>
    [Fact]
    public void CanRoute_OnlyWindowCommands()
    {
        var router = new WindowCommandRouter(new RecordingWindowCommands());

        Assert.True(router.CanRoute(HostCommandIds.WindowStartDrag));
        Assert.True(router.CanRoute(HostCommandIds.WindowStartResize));
        Assert.False(router.CanRoute(HostCommandIds.DiagnosticsExport));
        Assert.False(router.CanRoute(HostCommandIds.RecoveryExit));
    }

    /// <summary>非本路由的命令：抛 Ryn 命令未找到（与其它路由同款）。</summary>
    [Fact]
    public async Task RouteAsync_UnknownCommand_Throws()
    {
        var router = new WindowCommandRouter(new RecordingWindowCommands());

        await Assert.ThrowsAsync<CommandNotFoundException>(
            async () => await router.RouteAsync(HostCommandIds.DiagnosticsExport, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    /// <summary>UTF-8 参数帧。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>参数字节。</returns>
    private static ReadOnlyMemory<byte> Json(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    /// <summary>记录动作面（只记调用形状，不碰窗口）。</summary>
    private sealed class RecordingWindowCommands : IWindowCommands
    {
        /// <summary>调用序列。</summary>
        public List<string> Calls { get; } = [];

        /// <inheritdoc />
        public void BeginMove(int screenX, int screenY) => Calls.Add($"drag:{screenX},{screenY}");

        /// <inheritdoc />
        public void BeginResize(WindowEdgeKind edge, int screenX, int screenY) =>
            Calls.Add($"resize:{edge}:{screenX},{screenY}");

        /// <inheritdoc />
        public void ToggleMaximize() => Calls.Add(HostCommandIds.WindowToggleMaximize);

        /// <inheritdoc />
        public void Minimize() => Calls.Add(HostCommandIds.WindowMinimize);

        /// <inheritdoc />
        public void Close() => Calls.Add(HostCommandIds.WindowClose);

        /// <inheritdoc />
        public void SetFullscreen(bool fullscreen) => Calls.Add($"fullscreen:{fullscreen.ToString().ToLowerInvariant()}");
    }
}
