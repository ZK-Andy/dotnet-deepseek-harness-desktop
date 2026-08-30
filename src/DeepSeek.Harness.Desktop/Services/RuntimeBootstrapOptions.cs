using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 首启引导的可调参数（appsettings.json 的 <c>RuntimeBootstrap</c> 节，禁止硬编码进逻辑）。
/// ADR online-first-unbundled-runtime：无捆绑运行时且无 PATH dsh 时，引导下载钉版 Node +
/// registry 安装 dsh。闭包退役后本记录的 <see cref="NodeVersion"/> 是 Node 钉版的唯一正典。
/// </summary>
public sealed record RuntimeBootstrapOptions
{
    /// <summary>引导下载的 Node 钉版（闭包退役后的唯一正典；追平现役 LTS 属人工拍板）。</summary>
    public string NodeVersion { get; init; } = "24.20.0";

    /// <summary>Node 官方发行目录基址（含尾随 dist 段）。</summary>
    public string NodeDistBaseUrl { get; init; } = "https://nodejs.org/dist";

    /// <summary>Node 发行包多源回落镜像基址（默认 npmmirror；置空字符串 = 仅官方单源，镜像关闭）。
    /// 镜像仅承担归档下载，摘要仍取自 <see cref="NodeDistBaseUrl"/> 官方（防投毒，ADR reference-alignment 批次三）。</summary>
    public string NodeMirrorBaseUrl { get; init; } = "https://cdn.npmmirror.com/binaries/node";

    /// <summary>dsh 的安装 spec（默认跟 npm latest 解耦；当前钉 alpha.2，重入 @latest 见 ADR）。</summary>
    public string DshSpec { get; init; } = "@deepseek-ai/dsh@0.1.2-alpha.2";

    /// <summary>复用本机 PATH node 的最低主版本（低于则下载钉版）。</summary>
    public int MinimumLocalNodeMajor { get; init; } = 22;

    /// <summary>单个下载/安装步骤的超时（分钟）。</summary>
    public int StepTimeoutMinutes { get; init; } = 10;

    /// <summary>从应用旁的 appsettings.json 读取 <c>RuntimeBootstrap</c> 节；文件缺失或节缺失时全默认。</summary>
    public static RuntimeBootstrapOptions Load(string baseDirectory)
    {
        try
        {
            string path = Path.Combine(baseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return new RuntimeBootstrapOptions();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("RuntimeBootstrap", out JsonElement section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                return new RuntimeBootstrapOptions();
            }

            var options = new RuntimeBootstrapOptions();
            if (section.TryGetProperty(nameof(NodeVersion), out JsonElement v) && v.ValueKind == JsonValueKind.String)
            {
                options = options with { NodeVersion = v.GetString()! };
            }

            if (section.TryGetProperty(nameof(NodeDistBaseUrl), out JsonElement b) && b.ValueKind == JsonValueKind.String)
            {
                options = options with { NodeDistBaseUrl = b.GetString()! };
            }

            if (section.TryGetProperty(nameof(NodeMirrorBaseUrl), out JsonElement mb) && mb.ValueKind == JsonValueKind.String)
            {
                options = options with { NodeMirrorBaseUrl = mb.GetString()! };
            }

            if (section.TryGetProperty(nameof(DshSpec), out JsonElement s) && s.ValueKind == JsonValueKind.String)
            {
                options = options with { DshSpec = s.GetString()! };
            }

            if (section.TryGetProperty(nameof(MinimumLocalNodeMajor), out JsonElement m) && m.ValueKind == JsonValueKind.Number)
            {
                options = options with { MinimumLocalNodeMajor = m.GetInt32() };
            }

            if (section.TryGetProperty(nameof(StepTimeoutMinutes), out JsonElement t) && t.ValueKind == JsonValueKind.Number)
            {
                options = options with { StepTimeoutMinutes = t.GetInt32() };
            }

            return options;
        }
        catch (System.Text.Json.JsonException)
        {
            // 配置损坏不阻塞启动：回退全默认（与 UpdateOptions 同哲学——引导参数是增强配置）
            return new RuntimeBootstrapOptions();
        }
    }
}
