using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>ready.json 文件持久化：读（缺文件/损坏/字段缺失）、写往返、清幂等。</summary>
public class FileReadyPersistenceTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ddc-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>验证无 ready.json 时 GetAsync 返回 null（首次启动/从未就绪）。</summary>
    [Fact]
    public async Task GetAsync_NoFile_ReturnsNull()
    {
        string dir = NewDir();
        try
        {
            var p = new FileReadyPersistence(dir);
            Assert.Null(await p.GetAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>验证写入后可读回同版本/同资产路径的记录（往返契约）。</summary>
    [Fact]
    public async Task SetThenGet_RoundTripsRecord()
    {
        string dir = NewDir();
        try
        {
            var p = new FileReadyPersistence(dir);
            var record = new UpdateStateMachine.ReadyRecord("0.4.4", "/home/u/.dsh/updates/app.deb");
            await p.SetAsync(record, CancellationToken.None);

            UpdateStateMachine.ReadyRecord? loaded = await p.GetAsync(CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal("0.4.4", loaded.Version);
            Assert.Equal("/home/u/.dsh/updates/app.deb", loaded.AssetPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>验证写出的文件是合法 JSON 且键名 version/assetPath 与 GetAsync 读方互认（AOT 源生成契约）。</summary>
    [Fact]
    public async Task Set_WritesJsonWithContractKeys()
    {
        string dir = NewDir();
        try
        {
            await new FileReadyPersistence(dir).SetAsync(
                new UpdateStateMachine.ReadyRecord("0.4.4", "/a.deb"), CancellationToken.None);

            string raw = File.ReadAllText(Path.Combine(dir, "ready.json"));
            Assert.Contains("\"version\":\"0.4.4\"", raw);
            Assert.Contains("\"assetPath\":\"/a.deb\"", raw);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>验证文件内容损坏（非 JSON）时 GetAsync 返回 null 而非抛错——损坏记录视同不存在。</summary>
    [Theory]
    [InlineData("{not-json")]
    [InlineData("[]")]
    [InlineData("""{"version":5}""")]
    [InlineData("""{"version":"0.4.4"}""")]
    public async Task GetAsync_CorruptOrPartial_ReturnsNull(string content)
    {
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "ready.json"), content);
            var p = new FileReadyPersistence(dir);
            Assert.Null(await p.GetAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>验证 ClearAsync 删除 ready.json；再次 Clear 幂等不抛。</summary>
    [Fact]
    public async Task ClearAsync_RemovesFile_AndIsIdempotent()
    {
        string dir = NewDir();
        try
        {
            var p = new FileReadyPersistence(dir);
            await p.SetAsync(new UpdateStateMachine.ReadyRecord("0.4.4", "/a.deb"), CancellationToken.None);
            string path = Path.Combine(dir, "ready.json");
            Assert.True(File.Exists(path));

            await p.ClearAsync(CancellationToken.None);
            Assert.False(File.Exists(path));
            await p.ClearAsync(CancellationToken.None); // 幂等：无文件再清不抛
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
