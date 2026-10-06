using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 回环代理日志的可调参数（appsettings.json 的 <c>ProxyLogging</c> 节，ADR proxy-log-noise-reduction）。
/// 默认 = 降噪态：逐请求/逐隧道 trace 不落盘，只留异常面与收尾统计。
/// 装载语义与 <see cref="RuntimeTimeouts"/> 同款：缺文件/坏 JSON/缺节/键缺失/取值不可用一律回退默认，不阻塞启动。
/// </summary>
public sealed record ProxyLogging
{
    /// <summary>无人值守/冒烟腿显式打开逐请求 trace 的环境变量（先例 <c>DSH_DESKTOP_PREINSTALL_AUTO</c>）：
    /// 冒烟腿的存活门以逐请求行为判据，产品默认静默后由该面显式打开（ADR proxy-log-noise-reduction）。</summary>
    internal const string TraceEnv = "DSH_DESKTOP_PROXY_TRACE";

    private const int SlowRequestFloorMilliseconds = 1;

    /// <summary>逐请求/逐隧道 trace 开关（默认关）：打开恢复每请求两行与隧道建/收行，供时序类排障复现；
    /// 关闭时隧道收行的**故障收场**支仍落（异常面，见 ADR proxy-log-noise-reduction）。</summary>
    public bool Trace { get; init; }

    /// <summary>慢请求判据（毫秒，默认 2000；&lt;1 回默认）：成功请求耗时达到该阈值也落一行。</summary>
    public int SlowRequestMilliseconds { get; init; } = 2000;

    /// <summary>从 <paramref name="baseDirectory"/> 的 appsettings.json 装载，再叠加无人值守面的 trace 覆盖；
    /// 文件缺失/损坏/不可读一律回退默认，不阻塞启动。</summary>
    /// <param name="baseDirectory">appsettings.json 所在目录（宿主为 AppContext.BaseDirectory）。</param>
    public static ProxyLogging Load(string baseDirectory) =>
        ApplyTraceEnv(LoadFile(baseDirectory), Environment.GetEnvironmentVariable(TraceEnv));

    /// <summary>无人值守 trace 覆盖的纯判定（ADR proxy-log-noise-reduction）：仅字面 <c>"1"</c> 打开，
    /// 其余（未设/其他值）一律维持文件档位——fail-closed；拆纯缝以便不经进程环境单测。</summary>
    /// <param name="options">文件装载结果。</param>
    /// <param name="value">环境变量取值（null = 未设）。</param>
    internal static ProxyLogging ApplyTraceEnv(ProxyLogging options, string? value) =>
        value == "1" ? options with { Trace = true } : options;

    private static ProxyLogging LoadFile(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new ProxyLogging();
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 配置损坏/不可读不阻塞启动：回退全默认（与 RuntimeTimeouts/UpdateOptions 同哲学——日志档位是增强配置）。
            return new ProxyLogging();
        }
    }

    /// <summary>把 appsettings.json 全文解析为 <see cref="ProxyLogging"/>（纯函数，可单测；不含环境覆盖）：
    /// 根非对象、无 <c>ProxyLogging</c> 节、节非对象、键缺失、类型不符或取值越界（小数/超 Int32 范围/小于下界）
    /// 一律回退默认；损坏 JSON 由调用方转 fail-safe（本方法除坏 JSON 外不抛）。</summary>
    /// <param name="json">appsettings.json 全文。</param>
    /// <returns>解析后的选项（缺省字段保持默认值）。</returns>
    internal static ProxyLogging Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("ProxyLogging", out JsonElement section) ||
            section.ValueKind != JsonValueKind.Object)
        {
            // 根非对象（`[]`/`"x"`/`42`/`null`）时 TryGetProperty 会抛 InvalidOperationException——
            // 该形态属配置损坏，与缺节同等回默认，绝不升级成启动异常。
            return new ProxyLogging();
        }

        var options = new ProxyLogging();
        options = options with { Trace = GetBool(section, nameof(Trace), options.Trace) };
        options = options with
        {
            SlowRequestMilliseconds = GetInt(
                section, nameof(SlowRequestMilliseconds), options.SlowRequestMilliseconds, SlowRequestFloorMilliseconds),
        };
        return options;
    }

    private static bool GetBool(JsonElement section, string name, bool current) =>
        section.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : current;

    /// <summary>数值键读取：非数值、不可表示为 Int32（小数/越界——<c>GetInt32</c> 会抛 FormatException）或小于
    /// <paramref name="floor"/> 一律回退默认值，绝不把配置损坏升级成启动异常。</summary>
    private static int GetInt(JsonElement section, string name, int current, int floor) =>
        section.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed) &&
        parsed >= floor
            ? parsed
            : current;
}
