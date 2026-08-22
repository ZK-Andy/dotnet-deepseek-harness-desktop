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

    /// <summary>从应用旁的 appsettings.json 读取 <c>Update</c> 节；文件缺失或节缺失时全默认。</summary>
    public static UpdateOptions Load(string baseDirectory)
    {
        try
        {
            var path = Path.Combine(baseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return new UpdateOptions();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Update", out var section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                return new UpdateOptions();
            }

            var options = new UpdateOptions();
            if (section.TryGetProperty("Repository", out var repo) && repo.ValueKind == JsonValueKind.String)
            {
                options = options with { Repository = repo.GetString()! };
            }

            if (section.TryGetProperty("FeedTimeoutSeconds", out var feed) && feed.ValueKind == JsonValueKind.Number)
            {
                options = options with { FeedTimeoutSeconds = feed.GetInt32() };
            }

            if (section.TryGetProperty("DownloadTimeoutMinutes", out var dl) && dl.ValueKind == JsonValueKind.Number)
            {
                options = options with { DownloadTimeoutMinutes = dl.GetInt32() };
            }

            if (section.TryGetProperty("UpdatesDirName", out var dir) && dir.ValueKind == JsonValueKind.String)
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
