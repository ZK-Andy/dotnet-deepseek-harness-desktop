using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>自更新的可调参数（appsettings.json 的 <c>Update</c> 节，禁止硬编码进逻辑）。</summary>
public sealed record UpdateOptions
{
    /// <summary>GitHub 仓库（owner/name），release 与资产都从这里解析。</summary>
    public string Repository { get; init; } = "ZK-Andy/dotnet-deepseek-harness-desktop";

    /// <summary>feed/资产页抓取超时（秒）。</summary>
    public int FeedTimeoutSeconds { get; init; } = 15;

    /// <summary>安装包下载超时（分钟）。</summary>
    public int DownloadTimeoutMinutes { get; init; } = 30;

    /// <summary>下载与 ready 持久化目录；相对路径基于 DSH_HOME。</summary>
    public string UpdatesDirName { get; init; } = "updates";

    /// <summary>dev 运行时下显式开启自更新的环境变量（默认 dev 不装载更新栈，防误装系统包；仅供升级链路验证）。</summary>
    public const string ForceDevEnv = "DSH_DESKTOP_UPDATE_FORCE";

    /// <summary>是否装载自更新栈：非 dev 恒真；dev 需显式 <c>DSH_DESKTOP_UPDATE_FORCE=1</c>（纯判定可单测）。</summary>
    public static bool IsEnabledFor(bool isDev, string? forceDevEnv) => !isDev || forceDevEnv == "1";

    /// <summary>从应用旁的 appsettings.json 读取 <c>Update</c> 节；文件缺失或节缺失时全默认。</summary>
    public static UpdateOptions Load(string baseDirectory)
    {
        try
        {
            string path = Path.Combine(baseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return new UpdateOptions();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Update", out JsonElement section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                return new UpdateOptions();
            }

            var options = new UpdateOptions();
            if (section.TryGetProperty(nameof(Repository), out JsonElement repo) && repo.ValueKind == JsonValueKind.String)
            {
                options = options with { Repository = repo.GetString()! };
            }

            if (section.TryGetProperty(nameof(FeedTimeoutSeconds), out JsonElement feed) && feed.ValueKind == JsonValueKind.Number)
            {
                options = options with { FeedTimeoutSeconds = feed.GetInt32() };
            }

            if (section.TryGetProperty(nameof(DownloadTimeoutMinutes), out JsonElement dl) && dl.ValueKind == JsonValueKind.Number)
            {
                options = options with { DownloadTimeoutMinutes = dl.GetInt32() };
            }

            if (section.TryGetProperty(nameof(UpdatesDirName), out JsonElement dir) && dir.ValueKind == JsonValueKind.String)
            {
                options = options with { UpdatesDirName = dir.GetString()! };
            }

            return options;
        }
        catch (JsonException)
        {
            // 配置损坏不阻塞启动：回退全默认（fail-safe 而非 fail-loud——更新是增强功能）。
            return new UpdateOptions();
        }
    }
}
