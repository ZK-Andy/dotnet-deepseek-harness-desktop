using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>窗口几何存储（ADR 2026-10-25-window-identity-geometry-teardown）：round-trip、损坏回退与
/// 非法窗口态拒收。文件路径注入临时目录，不碰真实 profile。</summary>
public class WindowGeometryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dsh-geom-{Guid.NewGuid():N}");

    /// <summary>Save→Load round-trip 全字段一致。</summary>
    [Fact]
    public void SaveThenLoad_PreservesAllFields()
    {
        WindowGeometryStore store = CreateStore();
        var input = new WindowGeometry(101, 202, 1280.5, 860.25, WindowGeometryState.FullScreen);

        store.Save(input);

        WindowGeometry? loaded = store.Load();
        Assert.Equal(input, loaded);
    }

    /// <summary>无文件回 null（首启常态）。</summary>
    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        Assert.Null(CreateStore().Load());
    }

    /// <summary>损坏内容回 null，不抛。</summary>
    [Fact]
    public void Load_CorruptContent_ReturnsNull()
    {
        WindowGeometryStore store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, WindowGeometryStore.FileName), "{not json");

        Assert.Null(store.Load());
    }

    /// <summary>缺字段回 null（手工解析判据：五键齐全才认）。</summary>
    [Fact]
    public void Load_MissingField_ReturnsNull()
    {
        WindowGeometryStore store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, WindowGeometryStore.FileName),
            """{"x":1,"y":2,"width":1200,"height":800}""");

        Assert.Null(store.Load());
    }

    /// <summary>未知窗口态拒收（封闭集）。</summary>
    [Fact]
    public void Load_UnknownState_ReturnsNull()
    {
        WindowGeometryStore store = CreateStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, WindowGeometryStore.FileName),
            """{"x":1,"y":2,"width":1200,"height":800,"state":"minimized"}""");

        Assert.Null(store.Load());
    }

    /// <summary>覆盖写：后写覆盖先写。</summary>
    [Fact]
    public void Save_OverwritesPreviousValue()
    {
        WindowGeometryStore store = CreateStore();
        store.Save(new WindowGeometry(1, 2, 800, 600, WindowGeometryState.Normal));
        var second = new WindowGeometry(3, 4, 1024, 768, WindowGeometryState.Maximized);
        store.Save(second);

        Assert.Equal(second, store.Load());
    }

    private WindowGeometryStore CreateStore() => new(Path.Combine(_dir, WindowGeometryStore.FileName));

    /// <summary>清理临时目录（存储夹具）。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
