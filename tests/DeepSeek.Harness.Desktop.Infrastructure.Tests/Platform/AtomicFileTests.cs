namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>原子写单点：内容落盘、覆盖既有文件、失败不留临时文件。</summary>
public class AtomicFileTests
{
    /// <summary>验证内容逐字落盘，且成功后目录内不留临时文件（临时文件已改名成目标文件）。</summary>
    [Fact]
    public void WriteAllText_WritesContent_AndLeavesNoTempFile()
    {
        string dir = NewDir();
        try
        {
            string path = Path.Combine(dir, "state.json");
            AtomicFile.WriteAllText(path, "hello");
            Assert.Equal("hello", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证覆盖既有文件：内容整体替换而非追加。</summary>
    [Fact]
    public void WriteAllText_OverwritesExistingContent()
    {
        string dir = NewDir();
        try
        {
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "old-content");
            AtomicFile.WriteAllText(path, "new");
            Assert.Equal("new", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证目标目录缺失时抛出（建目录是调用点的语义）且不留残留文件。</summary>
    [Fact]
    public void WriteAllText_MissingDirectory_Throws_AndLeavesNoResidue()
    {
        string dir = NewDir();
        try
        {
            string missing = Path.Combine(dir, "absent", "state.json");
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(missing, "x"));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsh-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
