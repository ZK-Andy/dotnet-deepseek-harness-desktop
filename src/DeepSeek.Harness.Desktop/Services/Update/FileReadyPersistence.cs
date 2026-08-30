using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>ready 记录的 JSON 文件持久化（<c>&lt;updates 目录&gt;/ready.json</c>，跨启动恢复「可安装」）。</summary>
public sealed class FileReadyPersistence(string dir) : UpdateStateMachine.IPersistence
{
    private readonly string _path = Path.Combine(dir, "ready.json");

    /// <inheritdoc />
    public async Task<UpdateStateMachine.ReadyRecord?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("assetPath", out JsonElement asset) || asset.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return new UpdateStateMachine.ReadyRecord(version.GetString()!, asset.GetString()!);
        }
        catch (JsonException)
        {
            // 损坏的记录视同不存在
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(UpdateStateMachine.ReadyRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dir);
        // 经 AppJsonContext 源生成（AOT 安全）；键名 version/assetPath 与 GetAsync 读方及历史文件互认
        string json = JsonSerializer.Serialize(record, AppJsonContext.Default.ReadyRecord);
        await File.WriteAllTextAsync(_path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
