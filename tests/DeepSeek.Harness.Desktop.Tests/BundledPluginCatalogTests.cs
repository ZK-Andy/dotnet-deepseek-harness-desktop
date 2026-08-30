using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// BundledPluginCatalog 的行为回归（ADR bundled-plugin-version-aware-catalog +
/// online-first-unbundled-runtime 批次三）：清单装配判定（未装/落后即升/同版与更高跳过/
/// spec 缺失/副本损坏修复）+ 脏版本串与解析器异常的单项隔离 + 顺序契约。
/// 全部落盘文件收敛在每个用例的 root 临时目录下，finally 统一清理。
/// </summary>
public class BundledPluginCatalogTests
{
    private static string NewRoot() => Directory.CreateTempSubdirectory("bpc-").FullName;

    /// <summary>目录形态的随包 spec（ReadBundledVersion 直读其 package.json，无需 tgz）。</summary>
    private static string NewSpecDir(string root, string name, string version)
    {
        string dir = Path.Combine(root, "specs", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), $"{{\"version\":\"{version}\"}}");
        return dir;
    }

    /// <summary>构造含 dependencies + dsh.profile.bundles 的 profile 清单；未列出的包视为未装。</summary>
    private static string NewProfile(string root, params string[] bundlePkgs)
    {
        return NewProfileWithSpecs(root, bundlePkgs.Select(p => (p, $"file:/x/{p}.tgz")).ToArray());
    }

    /// <summary>构造含自定义 spec 值的 profile 清单（如 registry 形态 dependencies）。</summary>
    private static string NewProfileWithSpecs(string root, params (string Pkg, string Spec)[] deps)
    {
        string dir = Path.Combine(root, "profile");
        Directory.CreateDirectory(dir);
        string depsJson = string.Join(",\n    ", deps.Select(d => $"\"{d.Pkg}\": \"{d.Spec}\""));
        string bundles = string.Join(", ", deps.Select(d => $"\"{d.Pkg}\""));
        File.WriteAllText(
            Path.Combine(dir, "package.json"),
            "{\n  \"dependencies\": {\n    " + depsJson + "\n  },\n  \"dsh\": {\"profile\": {\"bundles\": [" + bundles + "]}}\n}");
        return dir;
    }

    private static void InstallCopy(string profileDir, string pkg, string version)
    {
        string pkgDir = Path.Combine(profileDir, "node_modules", pkg);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(
            Path.Combine(pkgDir, "package.json"),
            $"{{\"name\":\"{pkg}\",\"version\":\"{version}\"}}");
    }

    private static (List<(string Package, string Spec)> Pending, List<string> Logs) Assemble(
        IEnumerable<BundledPluginCatalog.Entry> catalog, string profileDir, string? pluginsDir = null)
    {
        var logs = new List<string>();
        List<(string Package, string Spec)> pending = BundledPluginCatalog.AssemblePending(
            catalog, pluginsDir, Path.Combine(profileDir, "package.json"), profileDir, logs.Add);
        return (pending, logs);
    }

    [Fact]
    public void NotInstalled_AddedToPendingWithoutVersionRead()
    {
        string root = NewRoot();
        try
        {
            string profile = NewProfile(root); // 空 profile = 什么都未装；spec 指向不存在的路径也不读
            BundledPluginCatalog.Entry[] catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => "/nonexistent/pkg-a.tgz") };

            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(catalog, profile);

            (string Package, string Spec) entry = Assert.Single(pending);
            Assert.Equal(("pkg-a", "/nonexistent/pkg-a.tgz"), (entry.Package, entry.Spec));
            Assert.Contains(logs, l => l.Contains("pkg-a") && l.Contains("未就位"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InstalledOlder_UpgradedWithVersionLog()
    {
        string root = NewRoot();
        try
        {
            string profile = NewProfile(root, "pkg-a");
            InstallCopy(profile, "pkg-a", "0.0.1");
            BundledPluginCatalog.Entry[] catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => NewSpecDir(root, "pkg-a", "0.0.2")) };

            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(catalog, profile);

            Assert.Single(pending);
            Assert.Equal("pkg-a", pending[0].Package);
            Assert.Contains(logs, l => l.Contains("随包插件升级") && l.Contains("0.0.1") && l.Contains("0.0.2"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("0.0.2")] // 同版：跳过
    [InlineData("0.0.3")] // 已装更高：绝不降级
    public void InstalledCurrentOrNewer_Skipped(string installedVersion)
    {
        string root = NewRoot();
        try
        {
            // 来源 spec 固定 0.0.2；已装 0.0.2 视为同版、已装 0.0.3 高于来源，均不得入列。
            string profile = NewProfile(root, "pkg-a");
            InstallCopy(profile, "pkg-a", installedVersion);
            BundledPluginCatalog.Entry[] catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => NewSpecDir(root, "pkg-a", "0.0.2")) };

            (List<(string Package, string Spec)>? pending, List<string> _) = Assemble(catalog, profile);

            Assert.Empty(pending);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BrokenOrMissingInstalledCopy_RepairedViaReinstall()
    {
        string root = NewRoot();
        try
        {
            // 已入 dependencies/bundles 但 node_modules 副本缺失 → 已装版本不可读 → 重装修复。
            string profile = NewProfile(root, "pkg-a");
            BundledPluginCatalog.Entry[] catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => NewSpecDir(root, "pkg-a", "0.0.9")) };

            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(catalog, profile);

            Assert.Single(pending);
            Assert.Contains(logs, l => l.Contains("(不可读)"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NullSpec_NoSourceForPlugin_Skipped()
    {
        string root = NewRoot();
        try
        {
            string profile = NewProfile(root);
            BundledPluginCatalog.Entry[] catalog = new[] { new BundledPluginCatalog.Entry("ghost", _ => null) };

            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(catalog, profile);

            Assert.Empty(pending);
            Assert.Contains(logs, l => l.Contains("ghost") && l.Contains("无可用来源"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DirtyBundledVersion_IsolatedToOwnEntry()
    {
        string root = NewRoot();
        try
        {
            string profile = NewProfile(root, "dirty", "healthy");
            InstallCopy(profile, "dirty", "1.0.0");
            InstallCopy(profile, "healthy", "1.0.0");

            BundledPluginCatalog.Entry[] catalog = new[]
            {
                new BundledPluginCatalog.Entry("dirty", _ => NewSpecDir(root, "dirty", "2.a.0")),
                new BundledPluginCatalog.Entry("healthy", _ => NewSpecDir(root, "healthy", "2.0.0")),
            };

            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(catalog, profile);

            // 脏版本串只跳过本插件的升级检查，不拖垮其余插件。
            (string Package, string Spec) entry = Assert.Single(pending);
            Assert.Equal("healthy", entry.Package);
            Assert.Contains(logs, l => l.Contains("dirty") && l.Contains("版本比对失败"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ThrowingResolver_IsolatedToOwnEntry()
    {
        string root = NewRoot();
        try
        {
            string profile = NewProfile(root, "healthy");
            InstallCopy(profile, "healthy", "1.0.0");

            BundledPluginCatalog.Entry[] catalog = new[]
            {
                new BundledPluginCatalog.Entry("boom", _ => throw new InvalidOperationException("boom")),
                new BundledPluginCatalog.Entry("healthy", _ => NewSpecDir(root, "healthy", "2.0.0")),
            };

            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(catalog, profile);

            // 清单扩展点下解析器实现出错：该插件跳过，其余清单项照常装配。
            (string Package, string Spec) entry = Assert.Single(pending);
            Assert.Equal("healthy", entry.Package);
            Assert.Contains(logs, l => l.Contains("boom") && l.Contains("spec 解析失败"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PendingOrder_FollowsCatalogOrder()
    {
        string root = NewRoot();
        try
        {
            string profile = NewProfile(root);
            BundledPluginCatalog.Entry[] catalog = new[]
            {
                new BundledPluginCatalog.Entry("second", _ => "/x/second.tgz"),
                new BundledPluginCatalog.Entry("first", _ => "/x/first.tgz"),
            };

            (List<(string Package, string Spec)>? pending, List<string> _) = Assemble(catalog, profile);

            Assert.Equal(["second", "first"], pending.Select(p => p.Package));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RealCatalog_CompanionFromInstallerPluginsDir_WhenRuntimeSeedsRetired()
    {
        string root = NewRoot();
        try
        {
            // 在线-first 打包新装形态：安装器资源 resources/plugins 是 companion 唯一供给源——
            // 运行时目录 tgz/目录种子已随闭包退役（解析器只认安装器资源）。
            string pluginsDir = Path.Combine(root, "plugins");
            Directory.CreateDirectory(pluginsDir);
            TestTarGz.Write(
                Path.Combine(pluginsDir, "dsh-desktop-companion.tgz"),
                ("package/package.json", """{"name":"dsh-desktop-companion","version":"9.9.7"}"""),
                ("package/lib/pad.js", RandomPad(2 * 1024)));

            string profile = NewProfile(root);
            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(
                BundledPluginCatalog.All, profile, pluginsDir: pluginsDir);

            // 运行时目录 tgz/目录分支退役：companion 只能来自安装器资源；dshmarket 不再随包
            // （catalog 仅 companion），故 pending 仅 companion 一条。（不装 dshmarket，市场由引导经 registry 安装。）
            (string Package, string Spec) entry = Assert.Single(pending);
            Assert.Equal(("dsh-desktop-companion", Path.Combine(pluginsDir, "dsh-desktop-companion.tgz")), (entry.Package, entry.Spec));
            Assert.DoesNotContain(pending, p => p.Package == "dshmarket");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RealCatalog_CompanionNull_WhenInstallerPluginsDirMissing()
    {
        string root = NewRoot();
        try
        {
            // 开发用 PATH dsh 场景（安装器资源与运行时目录均未携带 companion 种子）：
            // companion 无来源返回 null，catalog 的 companion 条目跳过、无人待装。
            string profile = NewProfile(root);
            (List<(string Package, string Spec)>? pending, List<string>? logs) = Assemble(BundledPluginCatalog.All, profile, pluginsDir: null);

            Assert.Empty(pending);
            Assert.Contains(logs, l => l.Contains("dsh-desktop-companion") && l.Contains("无可用来源"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>随机 Base64 填充串：高熵不可压缩，保证 tgz 磁盘体积达到闸值。</summary>
    private static string RandomPad(int length)
    {
        byte[] bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes)[..length];
    }
}
