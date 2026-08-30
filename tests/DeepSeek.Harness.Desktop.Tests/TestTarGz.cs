using System.Formats.Tar;
using System.IO.Compression;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>测试共享的随包插件 tgz 构造器（与 build-companion-tgz.sh 的 `tar -czf … package` 结构一致）。</summary>
internal static class TestTarGz
{
    /// <summary>把若干条目写入 <paramref name="path"/> 指定的 gzip+tar 包。</summary>
    public static string Write(string path, params (string EntryName, string Content)[] entries)
    {
        using (FileStream fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        using (var writer = new TarWriter(gz))
        {
            foreach ((string? name, string? content) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
                entry.DataStream = new MemoryStream(bytes);
                writer.WriteEntry(entry);
            }
        }

        return path;
    }
}
