using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 无边框窗口 chrome 的可调参数（appsettings.json 的 <c>CaptionBar</c> 节，禁止硬编码进逻辑）。
/// 默认 52 = 官方 macOS 侧栏顶条高度（红绿灯让位带；上游 darwin 呈现同值）——该值同时驱动
/// 侧栏顶部让位、宿主 chrome 高度变量与 Ryn 自动拖拽条。配色不在此列：让位带由侧栏自身填充色
/// 覆盖（无缝），红绿灯点色是 macOS 系统色（设计常量）。
/// </summary>
public sealed record CaptionBarOptions
{
    /// <summary>顶部 chrome（红绿灯让位带）高度（CSS 像素）。默认 52 = 官方 macOS 侧栏顶条。</summary>
    public int HeightPx { get; init; } = 52;

    /// <summary>全默认实例：组合根字段初始化的引用目标（目标类型 <c>new()</c> 会被组合根配额计数，
    /// C3 的 new 只留给真正的装配必要面）。</summary>
    public static CaptionBarOptions Default { get; } = new();

    /// <summary>appsettings 节名（类型名 CaptionBarOptions ≠ 节名，显式声明）。</summary>
    private const string SectionName = "CaptionBar";

    /// <summary>从应用旁的 appsettings.json 读取 <c>CaptionBar</c> 节；文件缺失或节缺失时全默认；
    /// 文件不可读/损坏/取值不可用也回退全默认并留痕，不阻塞启动。</summary>
    /// <param name="baseDirectory">appsettings.json 所在目录。</param>
    /// <param name="log">失败留痕出口（可空；启动路径传 <c>HostLog.Write</c>）。</param>
    public static CaptionBarOptions Load(string baseDirectory, Action<string>? log = null) =>
        ConfigSectionFile.LoadFile(baseDirectory, SectionName, Parse, new CaptionBarOptions(), log);

    /// <summary>把 appsettings.json 全文解析为 <see cref="CaptionBarOptions"/>（纯函数，可单测）：
    /// 根非对象、无 <c>CaptionBar</c> 节、节非对象、键缺失或取值不可表示一律回退默认；
    /// 损坏 JSON 由调用方转 fail-safe（本方法除坏 JSON 外不抛）。
    /// 非正值视作缺失回退默认（高度 ≤ 0 不构成合法拖拽条）。</summary>
    /// <param name="json">appsettings.json 全文。</param>
    /// <returns>解析后的选项（缺省字段保持默认值）。</returns>
    internal static CaptionBarOptions Parse(string json) =>
        ConfigSectionJson.Parse(json, SectionName, ParseSection, new CaptionBarOptions());

    private static CaptionBarOptions ParseSection(JsonElement section)
    {
        var options = new CaptionBarOptions();
        int height = ConfigSectionJson.GetInt(section, nameof(HeightPx), options.HeightPx);
        return height > 0 ? options with { HeightPx = height } : options;
    }
}
