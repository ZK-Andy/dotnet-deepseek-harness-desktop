using System.Formats.Tar;
using System.IO.Compression;

namespace DeepSeek.Harness.Desktop.Infrastructure.Plugins;

/// <summary>
/// 插件包元数据读取适配器（<see cref="IPluginPackageReader"/> 的 Infrastructure 实现；
/// ADR core-purity-boundaries）：读盘面（tgz 解包 / profile 清单）全在本层，Core 只做判定与解析。
/// </summary>
public sealed class PluginPackageReader : IPluginPackageReader
{
    /// <inheritdoc />
    public string? ReadBundledPackageJson(string spec)
    {
        if (!spec.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            string path = Path.Combine(spec, "package.json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        using FileStream fs = File.OpenRead(spec);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        while (reader.GetNextEntry() is { } entry)
        {
            // GNU tar 从 package/ 目录打包得 package/package.json；部分 tar 写 ./package/… 前缀
            string name = entry.Name;
            if (name != "package/package.json" && name != "./package/package.json")
            {
                continue;
            }

            if (entry.DataStream is null)
            {
                throw new InvalidDataException($"tgz 内 {name} 无数据流");
            }

            using var sr = new StreamReader(entry.DataStream);
            return sr.ReadToEnd();
        }

        throw new InvalidDataException($"tgz 内未找到 package/package.json：{spec}");
    }

    /// <inheritdoc />
    public string? ReadInstalledPackageJson(string profileDir, string packageName)
    {
        string pkgJson = Path.Combine(profileDir, "node_modules", packageName, "package.json");
        return ReadTextOrNull(pkgJson);
    }

    /// <inheritdoc />
    public string? ReadProfilePackageJson(string profilePkgPath) => ReadTextOrNull(profilePkgPath);

    /// <summary>读文本；缺失或读不出返回 null（profile 是可再生状态，按未知处理）。</summary>
    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 所吞：profile 文件不可读（权限/占用）——profile 是可再生状态，按「版本未知」处理（调用方走重装）
            return null;
        }
    }
}
