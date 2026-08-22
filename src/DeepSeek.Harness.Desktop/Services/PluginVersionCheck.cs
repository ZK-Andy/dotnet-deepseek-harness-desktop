using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>随包插件版本感知的纯逻辑（可单测）：读随包 spec 与 profile 已装副本的版本号，判定是否需要升级。</summary>
/// <remarks>随包侧（tgz/闭包目录）结构异常 fail loud 抛出——那是我们自己的产物，坏了必须可见；
/// 已装侧（profile node_modules）异常返回 <see langword="null"/> 视为未知并走重装修复——profile 是
/// 可再生的运行时状态。版本比较复用 <see cref="Update.UpdateVersion"/>（数字段逐段比）。</remarks>
public static class PluginVersionCheck
{
    /// <summary>读取随包插件的版本号：<c>.tgz</c> 解 gzip+tar 取 <c>package/package.json</c>；目录形态直读其 <c>package.json</c>。</summary>
    /// <exception cref="InvalidDataException">tgz 内找不到 <c>package/package.json</c> 或 version 字段缺失/非字符串。</exception>
    public static string ReadBundledVersion(string spec)
    {
        if (!spec.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPackageJsonVersion(Path.Combine(spec, "package.json"));
        }

        using var fs = File.OpenRead(spec);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        while (reader.GetNextEntry() is { } entry)
        {
            // GNU tar 从 package/ 目录打包得 package/package.json；部分 tar 写 ./package/… 前缀
            var name = entry.Name;
            if (name != "package/package.json" && name != "./package/package.json")
            {
                continue;
            }

            if (entry.DataStream is null)
            {
                throw new InvalidDataException($"tgz 内 {name} 无数据流");
            }

            using var sr = new StreamReader(entry.DataStream);
            return ReadPackageJsonVersionText(sr.ReadToEnd());
        }

        throw new InvalidDataException($"tgz 内未找到 package/package.json：{spec}");
    }

    /// <summary>读取 profile 内已安装插件副本（<c>node_modules/&lt;pkg&gt;/package.json</c>）的版本号。</summary>
    /// <returns>副本缺失、JSON 损坏或无有效 version 时返回 <see langword="null"/>（未知 → 调用方按需重装修复）。</returns>
    public static string? ReadInstalledVersion(string profileDir, string packageName)
    {
        var pkgJson = Path.Combine(profileDir, "node_modules", packageName, "package.json");
        try
        {
            if (!File.Exists(pkgJson))
            {
                return null;
            }

            return TryExtractVersion(File.ReadAllText(pkgJson));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>是否需要安装/升级：已装版本不可读（含未装），或随包版本更新（<see cref="Update.UpdateVersion.Compare"/> &gt; 0）。</summary>
    /// <exception cref="ArgumentException">任一版本号无法逐段解析（fail loud，由调用方转日志跳过）。</exception>
    public static bool NeedsUpgrade(string? installedVersion, string bundledVersion)
    {
        return installedVersion is null || Update.UpdateVersion.Compare(bundledVersion, installedVersion) > 0;
    }

    private static string ReadPackageJsonVersion(string path) => ReadPackageJsonVersionText(File.ReadAllText(path));

    private static string ReadPackageJsonVersionText(string text)
    {
        return TryExtractVersion(text)
            ?? throw new InvalidDataException("package.json 缺少有效的 version 字段");
    }

    private static string? TryExtractVersion(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("version", out var v) ||
            v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var version = v.GetString();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}
