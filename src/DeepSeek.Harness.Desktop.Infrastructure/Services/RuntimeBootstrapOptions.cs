using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 首启引导的可调参数（appsettings.json 的 <c>RuntimeBootstrap</c> 节，禁止硬编码进逻辑）。
/// ADR simple-shell-single-global-dsh：桌面是简单壳，依赖全机唯一的系统全局 node + 全局 dsh
/// （都在 PATH 上）：有系统 node 就用其 npm 执行 <c>npm install -g @deepseek-ai/dsh@alpha</c>；没有则
/// 下载最新官方 node 发行包并装到系统全局前缀（需 sudo 时提示手动命令），再装全局 dsh。
/// </summary>
public sealed record RuntimeBootstrapOptions
{
    /// <summary>dsh 的安装 spec（跟随 alpha 预发布通道，见 ADR simple-shell-single-global-dsh）。</summary>
    public string DshSpec { get; init; } = "@deepseek-ai/dsh@alpha";

    /// <summary>单个下载/安装步骤的超时（分钟）。</summary>
    public int StepTimeoutMinutes { get; init; } = 10;

    /// <summary>node 官方发行目录基址（含尾随 dist 段）。</summary>
    public string NodeDistBaseUrl { get; init; } = "https://nodejs.org/dist";

    /// <summary>node 发行包多源回落镜像基址（默认 npmmirror；置空字符串 = 仅官方单源，镜像关闭）。
    /// 镜像仅承担归档下载，可信摘要仍取自 <see cref="NodeDistBaseUrl"/> 官方（防投毒）。</summary>
    public string NodeMirrorBaseUrl { get; init; } = "https://cdn.npmmirror.com/binaries/node";

    /// <summary>系统全局 node 安装前缀（空 = 默认 <see cref="RuntimeBootstrap.DefaultGlobalNodePrefix"/>）。
    /// 无系统 node 时桌面据此把下载的 node 落到系统全局位（用户可写，避免需 sudo；也可配 <c>/usr/local</c>）。</summary>
    public string NodeGlobalPrefix { get; init; } = string.Empty;

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
            if (section.TryGetProperty(nameof(DshSpec), out JsonElement s) && s.ValueKind == JsonValueKind.String)
            {
                options = options with { DshSpec = s.GetString()! };
            }

            if (section.TryGetProperty(nameof(StepTimeoutMinutes), out JsonElement t) && t.ValueKind == JsonValueKind.Number)
            {
                options = options with { StepTimeoutMinutes = t.GetInt32() };
            }

            if (section.TryGetProperty(nameof(NodeDistBaseUrl), out JsonElement b) && b.ValueKind == JsonValueKind.String)
            {
                options = options with { NodeDistBaseUrl = b.GetString()! };
            }

            if (section.TryGetProperty(nameof(NodeMirrorBaseUrl), out JsonElement mb) && mb.ValueKind == JsonValueKind.String)
            {
                options = options with { NodeMirrorBaseUrl = mb.GetString()! };
            }

            if (section.TryGetProperty(nameof(NodeGlobalPrefix), out JsonElement gp) && gp.ValueKind == JsonValueKind.String)
            {
                options = options with { NodeGlobalPrefix = gp.GetString()! };
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
