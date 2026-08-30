using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>RuntimeLocator 行为：运行时目录解析 + 捆绑运行时探测。</summary>
public class RuntimeLocatorTests
{
    /// <summary>验证 DSH_DESKTOP_RUNTIME_DIR 覆盖时运行时目录解析直接返回该变量的全路径。</summary>
    [Fact]
    public void ResolveRuntimeDirectory_EnvOverride_Wins()
    {
        string? prior = Environment.GetEnvironmentVariable("DSH_DESKTOP_RUNTIME_DIR");
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

    /// <summary>验证捆绑目录中就位 node 可执行文件与 dsh 入口 bin.js 时，TryLocateBundled 返回两者的绝对路径而非 null。</summary>
    [Fact]
    public void TryLocateBundled_WhenFilesPresent_ReturnsPaths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib"));
        File.WriteAllText(Path.Combine(dir, "node"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"), "// dsh\n");
        try
        {
            (string NodeExe, string DshEntry)? located = RuntimeLocator.TryLocateBundled(dir);
            Assert.NotNull(located);
            Assert.Equal(Path.Combine(dir, "node"), located!.Value.NodeExe);
            Assert.Equal(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"), located.Value.DshEntry);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证空目录中不存在捆绑运行时布局时 TryLocateBundled 返回 null（缺捆绑时仍可依赖下载路径兜底）。</summary>
    [Fact]
    public void TryLocateBundled_WhenMissing_ReturnsNull()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>验证 Windows 发行布局（node.exe 命名、不做扩展名猜测）下 TryLocateBundled 仍能命中并返回 node.exe 路径。</summary>
    [Fact]
    public void TryLocateBundled_WindowsNodeExeLayout_ReturnsPaths()
    {
        // 回归：Windows 发行包布局为 node.exe（File.Exists 不做扩展名探测，缺失此分支时
        // Windows 捆绑运行时永远定位不到——v0.3.12 及之前为潜伏 bug）
        string dir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib"));
        File.WriteAllText(Path.Combine(dir, "node.exe"), "MZ");
        File.WriteAllText(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"), "// dsh\n");
        try
        {
            (string NodeExe, string DshEntry)? located = RuntimeLocator.TryLocateBundled(dir);
            Assert.NotNull(located);
            Assert.Equal(Path.Combine(dir, "node.exe"), located!.Value.NodeExe);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>验证下载目录环境变量覆盖时 ResolveDownloadedRuntimeDirectory 返回其全路径，忽略默认下载位置。</summary>
    [Fact]
    public void ResolveDownloadedRuntimeDirectory_EnvOverride_Wins()
    {
        string? prior = Environment.GetEnvironmentVariable(RuntimeLocator.DownloadDirEnv);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeLocator.DownloadDirEnv, "/tmp/dl-runtime-x");
            Assert.Equal(Path.GetFullPath("/tmp/dl-runtime-x"), RuntimeLocator.ResolveDownloadedRuntimeDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeLocator.DownloadDirEnv, prior);
        }
    }

    /// <summary>验证捆绑目录未就位时统一解析回退到下载目录；两处皆缺（下载目录删除后）返回 null，只读探测不创建目录。</summary>
    [Fact]
    public void TryLocateRuntimeDirectory_FallsBackToDownloadedDir()
    {
        string? priorBundled = Environment.GetEnvironmentVariable(RuntimeLocator.BundledDirEnv);
        string? priorDownload = Environment.GetEnvironmentVariable(RuntimeLocator.DownloadDirEnv);
        string bundledDir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        string downloadDir = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 捆绑目录不存在 + 下载目录就位 → 统一解析命中下载目录
            Environment.SetEnvironmentVariable(RuntimeLocator.BundledDirEnv, bundledDir);
            Environment.SetEnvironmentVariable(RuntimeLocator.DownloadDirEnv, downloadDir);
            Directory.CreateDirectory(Path.Combine(downloadDir, "node_modules", "@deepseek-ai", "dsh", "lib"));
            File.WriteAllText(Path.Combine(downloadDir, "node"), "#!/bin/sh\n");
            File.WriteAllText(Path.Combine(downloadDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"), "// dsh\n");

            Assert.Equal(Path.GetFullPath(downloadDir), RuntimeLocator.TryLocateRuntimeDirectory());
            Assert.NotNull(RuntimeLocator.TryLocateBundled(RuntimeLocator.TryLocateRuntimeDirectory()!));

            // 两处都未就位 → null（只读探测）
            Directory.Delete(downloadDir, recursive: true);
            Assert.Null(RuntimeLocator.TryLocateRuntimeDirectory());
            Assert.Null(RuntimeLocator.TryLocateRuntimeDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeLocator.BundledDirEnv, priorBundled);
            Environment.SetEnvironmentVariable(RuntimeLocator.DownloadDirEnv, priorDownload);
            if (Directory.Exists(bundledDir))
            {
                Directory.Delete(bundledDir, recursive: true);
            }

            if (Directory.Exists(downloadDir))
            {
                Directory.Delete(downloadDir, recursive: true);
            }
        }
    }
}
