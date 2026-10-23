using System.Text.Json;
using DeepSeek.Harness.Desktop.Core.Ipc;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>窗口缩放边（页面 → 宿主的强类型面；与 Avalonia 的 <c>WindowEdge</c> 解耦，路由可 headless 单测）。</summary>
internal enum WindowEdgeKind
{
    /// <summary>左边缘。</summary>
    Left,

    /// <summary>右边缘。</summary>
    Right,

    /// <summary>上边缘。</summary>
    Top,

    /// <summary>下边缘。</summary>
    Bottom,

    /// <summary>左上角。</summary>
    TopLeft,

    /// <summary>右上角。</summary>
    TopRight,

    /// <summary>左下角。</summary>
    BottomLeft,

    /// <summary>右下角。</summary>
    BottomRight,
}

/// <summary>
/// 窗口命令的宿主动作面（ADR 2026-10-23-avalonia-caption-bar）：路由只认这六个动作，
/// 拖拽/缩放的平台互操作藏在实现里（<c>Hosting.AvaloniaWindowCommands</c>），故路由本身可 headless 测。
/// 坐标一律屏幕坐标（页面用 <c>window.screenX + clientX</c> 折算）——X11 的
/// <c>_NET_WM_MOVERESIZE</c> 与 Windows 的命中测试都按屏幕坐标工作。
/// </summary>
internal interface IWindowCommands
{
    /// <summary>开始拖拽窗口。</summary>
    /// <param name="screenX">按下点的屏幕 X。</param>
    /// <param name="screenY">按下点的屏幕 Y。</param>
    void BeginMove(int screenX, int screenY);

    /// <summary>开始缩放窗口。</summary>
    /// <param name="edge">缩放边。</param>
    /// <param name="screenX">按下点的屏幕 X。</param>
    /// <param name="screenY">按下点的屏幕 Y。</param>
    void BeginResize(WindowEdgeKind edge, int screenX, int screenY);

    /// <summary>最大化/还原切换。</summary>
    void ToggleMaximize();

    /// <summary>最小化。</summary>
    void Minimize();

    /// <summary>关闭窗口（走关窗闸门语义，与托盘退出同一条路）。</summary>
    void Close();

    /// <summary>进入/退出原生全屏。</summary>
    /// <param name="fullscreen">true = 进入全屏。</param>
    void SetFullscreen(bool fullscreen);
}

/// <summary>
/// <c>window.*</c> 命令路由（ADR 2026-10-23-avalonia-caption-bar）：Ryn 宿主里这些是 Ryn 窗口能力
/// （<c>ryn.json</c> 的 <c>window</c> 节）的语义；新宿主没有那层能力闸，改由本路由承接同一批命令名，
/// 页面侧脚本因此只换通道不换语义。参数帧经源生成上下文（<see cref="AppJsonContext"/>），
/// 解析失败回错误帧而不是抛 IPC 异常——页面按 error 展示。
/// </summary>
internal sealed class WindowCommandRouter : ICommandRouter
{
    private static readonly Dictionary<string, WindowEdgeKind> s_edges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["left"] = WindowEdgeKind.Left,
        ["right"] = WindowEdgeKind.Right,
        ["top"] = WindowEdgeKind.Top,
        ["bottom"] = WindowEdgeKind.Bottom,
        ["top-left"] = WindowEdgeKind.TopLeft,
        ["top-right"] = WindowEdgeKind.TopRight,
        ["bottom-left"] = WindowEdgeKind.BottomLeft,
        ["bottom-right"] = WindowEdgeKind.BottomRight,
    };

    private readonly IWindowCommands _commands;
    private readonly Action<string>? _log;

    /// <summary>创建路由。</summary>
    /// <param name="commands">宿主动作面。</param>
    /// <param name="log">日志委托（可选）。</param>
    public WindowCommandRouter(IWindowCommands commands, Action<string>? log = null)
    {
        _commands = commands;
        _log = log;
    }

    /// <inheritdoc />
    public bool CanRoute(string command) => command is HostCommandIds.WindowStartDrag
        or HostCommandIds.WindowStartResize
        or HostCommandIds.WindowToggleMaximize
        or HostCommandIds.WindowMinimize
        or HostCommandIds.WindowClose
        or HostCommandIds.WindowSetFullscreen;

    /// <inheritdoc />
    public ValueTask<string> RouteAsync(
        string command, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case HostCommandIds.WindowStartDrag:
                if (!TryReadPoint(args, out int dragX, out int dragY))
                {
                    return ValueTask.FromResult(Fail("拖拽参数不合法"));
                }

                _commands.BeginMove(dragX, dragY);
                return ValueTask.FromResult("{}");

            case HostCommandIds.WindowStartResize:
                if (!TryReadResize(args, out string edgeText, out int resizeX, out int resizeY))
                {
                    return ValueTask.FromResult(Fail("缩放参数不合法"));
                }

                if (!s_edges.TryGetValue(edgeText, out WindowEdgeKind edge))
                {
                    return ValueTask.FromResult(Fail("缩放边不合法：" + edgeText));
                }

                _commands.BeginResize(edge, resizeX, resizeY);
                return ValueTask.FromResult("{}");

            case HostCommandIds.WindowSetFullscreen:
                if (!TryReadFullscreen(args, out bool fullscreen))
                {
                    return ValueTask.FromResult(Fail("全屏参数不合法"));
                }

                _commands.SetFullscreen(fullscreen);
                return ValueTask.FromResult("{}");

            case HostCommandIds.WindowToggleMaximize:
                _commands.ToggleMaximize();
                return ValueTask.FromResult("{}");

            case HostCommandIds.WindowMinimize:
                _commands.Minimize();
                return ValueTask.FromResult("{}");

            case HostCommandIds.WindowClose:
                _commands.Close();
                return ValueTask.FromResult("{}");

            default:
                throw new RynCommandNotFoundException(command);
        }
    }

    /// <summary>参数非法：留痕并回错误帧（页面侧会吞掉 Promise 拒绝，不在这里记就两端都无证据）。</summary>
    /// <param name="message">人读原因。</param>
    /// <returns>错误帧 JSON。</returns>
    private string Fail(string message)
    {
        _log?.Invoke($"[host] 窗口命令参数非法：{message}");
        return AppJsonContext.Error(message);
    }

    /// <summary>解析 <c>{x,y}</c> 拖拽帧。</summary>
    /// <param name="args">参数原文。</param>
    /// <param name="x">屏幕 X。</param>
    /// <param name="y">屏幕 Y。</param>
    /// <returns>解析成功为 true。</returns>
    private bool TryReadPoint(ReadOnlyMemory<byte> args, out int x, out int y)
    {
        x = 0;
        y = 0;
        using JsonDocument? doc = Parse(args);
        return doc is not null
            && doc.RootElement.ValueKind == JsonValueKind.Object
            && TryInt(doc.RootElement, "x", out x)
            && TryInt(doc.RootElement, "y", out y);
    }

    /// <summary>解析 <c>{edge,x,y}</c> 缩放帧。</summary>
    /// <param name="args">参数原文。</param>
    /// <param name="edge">边名（<c>top-left</c> 形态）。</param>
    /// <param name="x">屏幕 X。</param>
    /// <param name="y">屏幕 Y。</param>
    /// <returns>解析成功为 true。</returns>
    private bool TryReadResize(ReadOnlyMemory<byte> args, out string edge, out int x, out int y)
    {
        edge = string.Empty;
        x = 0;
        y = 0;
        using JsonDocument? doc = Parse(args);
        return doc is not null
            && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("edge", out JsonElement edgeElement)
            && edgeElement.ValueKind == JsonValueKind.String
            && (edge = edgeElement.GetString() ?? string.Empty).Length > 0
            && TryInt(doc.RootElement, "x", out x)
            && TryInt(doc.RootElement, "y", out y);
    }

    /// <summary>解析 <c>{fullscreen}</c> 帧。</summary>
    /// <param name="args">参数原文。</param>
    /// <param name="fullscreen">是否进入全屏。</param>
    /// <returns>解析成功为 true。</returns>
    private bool TryReadFullscreen(ReadOnlyMemory<byte> args, out bool fullscreen)
    {
        fullscreen = false;
        using JsonDocument? doc = Parse(args);
        if (doc is null
            || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("fullscreen", out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        fullscreen = element.GetBoolean();
        return true;
    }

    /// <summary>读数字字段（页面在 HiDPI 下会发小数坐标，按四舍五入取整；缺字段或类型不对即失败）。</summary>
    /// <param name="root">对象根。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">读出的整数值。</param>
    /// <returns>成功为 true。</returns>
    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (!element.TryGetDouble(out double number))
        {
            return false;
        }

        value = (int)Math.Round(number);
        return true;
    }

    /// <summary>解析参数原文；空帧/非法 JSON 回 null（调用方回错误帧，失败只留痕）。</summary>
    /// <param name="args">参数原文。</param>
    /// <returns>文档；失败为 null。</returns>
    private JsonDocument? Parse(ReadOnlyMemory<byte> args)
    {
        if (args.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(args);
        }
        catch (JsonException ex)
        {
            _log?.Invoke($"[host] 窗口命令参数解析失败：{ex.Message}");
            return null;
        }
    }
}
