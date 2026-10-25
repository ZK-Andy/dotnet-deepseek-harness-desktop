using System.Text.Json;
using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 主窗几何持久化的文件实现（<see cref="IWindowGeometryStore"/>）：落在 desktop profile 状态目录
/// （<c>~/.dsh/profiles/dotnet-desktop/window-state.json</c>，与端口/PID 记忆同族；沿用 Ryn 内建
/// PersistWindowState 时代的文件名）。读失败或内容损坏回 null（调用方回退硬编码缺省）；写失败仅告警。
/// 线程安全：窗口态变化（UI 线程）与退出路径（线程池）并发读写。
/// </summary>
/// <param name="filePath">状态文件绝对路径。</param>
/// <param name="log">日志回调（可选；生产传 HostLog.Write）。</param>
public sealed class WindowGeometryStore(string filePath, Action<string>? log = null) : IWindowGeometryStore
{
    /// <summary>状态文件名（profile 状态目录下，Ryn 时代同名）。</summary>
    public const string FileName = "window-state.json";

    private readonly object _lock = new();

    /// <summary>生产用默认路径（desktop profile 状态目录）。</summary>
    /// <returns>状态文件绝对路径。</returns>
    public static string DefaultFilePath() => HarnessRuntimeHost.ResolveProfileStatePath(FileName);

    /// <summary>读回上次记住的几何；无记录、不可读或损坏一律 null。</summary>
    /// <returns>几何快照；无有效记录为 null。</returns>
    public WindowGeometry? Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("x", out JsonElement x)
                || !doc.RootElement.TryGetProperty("y", out JsonElement y)
                || !doc.RootElement.TryGetProperty("width", out JsonElement width)
                || !doc.RootElement.TryGetProperty("height", out JsonElement height)
                || !doc.RootElement.TryGetProperty("state", out JsonElement state)
                || state.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string stateValue = state.GetString() ?? string.Empty;
            WindowGeometryState? parsed = stateValue switch
            {
                // 文件格式串（camelCase 文件里的封闭集）→ 强类型态；未知值拒收整条记录
                "normal" => WindowGeometryState.Normal,
                "maximized" => WindowGeometryState.Maximized,
                "fullscreen" => WindowGeometryState.FullScreen,
                _ => null,
            };
            return parsed is { } state2
                ? new WindowGeometry(x.GetDouble(), y.GetDouble(), width.GetDouble(), height.GetDouble(), state2)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or InvalidOperationException)
        {
            // 首启无文件是常态；损坏不阻断启动——回退硬编码缺省（fail loud 无意义）
            log?.Invoke($"[host] 读取窗口几何失败（将回退缺省尺寸）：{ex.Message}");
            return null;
        }
    }

    /// <summary>记住本次几何（尽力而为；写失败仅导致下次启动回退缺省）。</summary>
    /// <param name="geometry">当前几何快照。</param>
    public void Save(WindowGeometry geometry)
    {
        lock (_lock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 经 InfrastructureJsonContext 源生成（AOT 安全）。Load 侧不走源生成反序列化的取舍：
                // 反序列化对缺字段落默认值（0/空）会被 Apply 部分还原出半截几何，手工解析五键齐全才认；
                // 该语义差由 WindowGeometryStoreTests.Load_MissingField 钉住
                AtomicFile.WriteAllText(
                    filePath,
                    JsonSerializer.Serialize(
                        new GeometryFile(
                            geometry.X,
                            geometry.Y,
                            geometry.Width,
                            geometry.Height,
                            geometry.State.ToString().ToLowerInvariant()),
                        InfrastructureJsonContext.Default.GeometryFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写失败仅影响下次启动的窗口起点，不阻断本次运行
                log?.Invoke($"[host] 写窗口几何失败（下次启动回退缺省尺寸）：{ex.Message}");
            }
        }
    }

    /// <summary>window-state.json 落盘帧；internal 供 <see cref="InfrastructureJsonContext"/> 源生成注册。</summary>
    /// <param name="X">窗口左上角 X。</param>
    /// <param name="Y">窗口左上角 Y。</param>
    /// <param name="Width">宽度（DIP）。</param>
    /// <param name="Height">高度（DIP）。</param>
    /// <param name="State">窗口态（normal/maximized/fullscreen）。</param>
    internal sealed record GeometryFile(double X, double Y, double Width, double Height, string State);
}
