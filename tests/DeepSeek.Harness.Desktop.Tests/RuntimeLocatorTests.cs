using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>RuntimeLocator 行为：运行时目录解析 + 捆绑运行时探测。</summary>
public class RuntimeLocatorTests
{
    [Fact]
    public void ResolveRuntimeDirectory_EnvOverride_Wins()
    {
        var prior = Environment.GetEnvironmentVariable("DSH_DESKTOP_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_RUNTIME_DIR", "/tmp/runtime-x");
            Assert.Equal(Path.GetFullPath("/tmp/runtime-x"), RuntimeLocator.ResolveRuntimeDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_DESKTOP_RUNTIME_DIR", prior);
        }
    }

    [Fact]
    public void TryLocateBundled_WhenFilesPresent_ReturnsPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib"));
        File.WriteAllText(Path.Combine(dir, "node"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"), "// dsh\n");
        try
        {
            var located = RuntimeLocator.TryLocateBundled(dir);
            Assert.NotNull(located);
            Assert.Equal(Path.Combine(dir, "node"), located!.Value.NodeExe);
            Assert.Equal(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"), located.Value.DshEntry);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLocateBundled_WhenMissing_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(RuntimeLocator.TryLocateBundled(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
