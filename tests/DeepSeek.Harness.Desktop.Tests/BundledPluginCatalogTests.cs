using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// BundledPluginCatalog 的行为回归（ADR bundled-plugin-version-aware-catalog）：
/// 清单装配判定（未装/落后即升/同版与更高跳过/spec 缺失/副本损坏修复）+ 脏版本串与
/// 解析器异常的单项隔离 + registry 回退放弃升级检查 + 顺序契约。
/// 全部落盘文件收敛在每个用例的 root 临时目录下，finally 统一清理。
/// </summary>
public class BundledPluginCatalogTests
{
    /// <summary>假 catalog 忽略 runtimeDir 参数，用常量避免无意义的临时目录创建。</summary>
    private const string UnusedRuntime = "/unused-runtime";

    private static string NewRoot() => Directory.CreateTempSubdirectory("bpc-").FullName;

    /// <summary>目录形态的随包 spec（ReadBundledVersion 直读其 package.json，无需 tgz）。</summary>
    private static string NewSpecDir(string root, string name, string version)
    {
        var dir = Path.Combine(root, "specs", name);
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
        var dir = Path.Combine(root, "profile");
        Directory.CreateDirectory(dir);
        var depsJson = string.Join(",\n    ", deps.Select(d => $"\"{d.Pkg}\": \"{d.Spec}\""));
        var bundles = string.Join(", ", deps.Select(d => $"\"{d.Pkg}\""));
        File.WriteAllText(
            Path.Combine(dir, "package.json"),
            "{\n  \"dependencies\": {\n    " + depsJson + "\n  },\n  \"dsh\": {\"profile\": {\"bundles\": [" + bundles + "]}}\n}");
        return dir;
    }

    private static void InstallCopy(string profileDir, string pkg, string version)
    {
        var pkgDir = Path.Combine(profileDir, "node_modules", pkg);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(
            Path.Combine(pkgDir, "package.json"),
            $"{{\"name\":\"{pkg}\",\"version\":\"{version}\"}}");
    }

    private static (List<(string Package, string Spec, bool FromRegistry)> Pending, List<string> Logs) Assemble(
        IEnumerable<BundledPluginCatalog.Entry> catalog, string runtimeDir, string profileDir)
    {
        var logs = new List<string>();
        var pending = BundledPluginCatalog.AssemblePending(
            catalog, runtimeDir, Path.Combine(profileDir, "package.json"), profileDir, logs.Add);
        return (pending, logs);
    }

    [Fact]
    public void NotInstalled_AddedToPendingWithoutVersionRead()
    {
        var root = NewRoot();
        try
        {
            var profile = NewProfile(root); // 空 profile = 什么都未装；spec 指向不存在的路径也不读
            var catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => "/nonexistent/pkg-a.tgz") };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            var entry = Assert.Single(pending);
            Assert.Equal(("pkg-a", "/nonexistent/pkg-a.tgz"), (entry.Package, entry.Spec));
            Assert.Contains(logs, l => l.Contains("pkg-a") && l.Contains("未就位"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InstalledOlder_UpgradedWithVersionLog()
    {
        var root = NewRoot();
        try
        {
            var profile = NewProfile(root, "pkg-a");
            InstallCopy(profile, "pkg-a", "0.0.1");
            var catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => NewSpecDir(root, "pkg-a", "0.0.2")) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

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
        var root = NewRoot();
        try
        {
            // 闭包钉版固定 0.0.2；已装 0.0.2 视为同版、已装 0.0.3 高于闭包，均不得入列。
            var profile = NewProfile(root, "pkg-a");
            InstallCopy(profile, "pkg-a", installedVersion);
            var catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => NewSpecDir(root, "pkg-a", "0.0.2")) };

            var (pending, _) = Assemble(catalog, UnusedRuntime, profile);

            Assert.Empty(pending);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BrokenOrMissingInstalledCopy_RepairedViaReinstall()
    {
        var root = NewRoot();
        try
        {
            // 已入 dependencies/bundles 但 node_modules 副本缺失 → 已装版本不可读 → 重装修复。
            var profile = NewProfile(root, "pkg-a");
            var catalog = new[] { new BundledPluginCatalog.Entry("pkg-a", _ => NewSpecDir(root, "pkg-a", "0.0.9")) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            Assert.Single(pending);
            Assert.Contains(logs, l => l.Contains("(不可读)"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NullSpec_ClosureWithoutPlugin_Skipped()
    {
        var root = NewRoot();
        try
        {
            var profile = NewProfile(root);
            var catalog = new[] { new BundledPluginCatalog.Entry("ghost", _ => null) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            Assert.Empty(pending);
            Assert.Contains(logs, l => l.Contains("ghost") && l.Contains("本闭包未携带"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DirtyBundledVersion_IsolatedToOwnEntry()
    {
        var root = NewRoot();
        try
        {
            var profile = NewProfile(root, "dirty", "healthy");
            InstallCopy(profile, "dirty", "1.0.0");
            InstallCopy(profile, "healthy", "1.0.0");

            var catalog = new[]
            {
                new BundledPluginCatalog.Entry("dirty", _ => NewSpecDir(root, "dirty", "2.a.0")),
                new BundledPluginCatalog.Entry("healthy", _ => NewSpecDir(root, "healthy", "2.0.0")),
            };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            // 脏版本串只跳过本插件的升级检查，不拖垮其余插件。
            var entry = Assert.Single(pending);
            Assert.Equal("healthy", entry.Package);
            Assert.Contains(logs, l => l.Contains("dirty") && l.Contains("版本比对失败"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ThrowingResolver_IsolatedToOwnEntry()
    {
        var root = NewRoot();
        try
        {
            var profile = NewProfile(root, "healthy");
            InstallCopy(profile, "healthy", "1.0.0");

            var catalog = new[]
            {
                new BundledPluginCatalog.Entry("boom", _ => throw new InvalidOperationException("boom")),
                new BundledPluginCatalog.Entry("healthy", _ => NewSpecDir(root, "healthy", "2.0.0")),
            };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            // 清单扩展点下解析器实现出错：该插件跳过，其余清单项照常装配。
            var entry = Assert.Single(pending);
            Assert.Equal("healthy", entry.Package);
            Assert.Contains(logs, l => l.Contains("boom") && l.Contains("spec 解析失败"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Installed_RegistryFallbackSpec_SkipsUpgradeCheck()
    {
        var root = NewRoot();
        try
        {
            // 已装 + registry 回退串（非路径）：无法与闭包内容比对时保守不动既有安装，
            // 只记日志放弃升级检查——fail-safe 分支的负控钉子。
            var profile = NewProfile(root, "dshmarket");
            InstallCopy(profile, "dshmarket", "1.0.0");
            var catalog = new[] { new BundledPluginCatalog.Entry("dshmarket", _ => "dshmarket@9.9.9") };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            Assert.Empty(pending);
            Assert.Contains(logs, l => l.Contains("dshmarket") && l.Contains("版本比对失败"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RegistryShapedInstalled_HandsOff_EvenWhenBundledNewer()
    {
        var root = NewRoot();
        try
        {
            // registry 形态已装 = 用户侧/registry 所有：即使闭包钉版远高于已装版本也不回拉
            // （ADR bundled-plugin-registry-normalization——回拉会让市场的更新检查与自更新再次失效）。
            var profile = NewProfileWithSpecs(root, ("dshmarket", "^1.0.0"));
            InstallCopy(profile, "dshmarket", "1.0.0");
            var catalog = new[] { new BundledPluginCatalog.Entry("dshmarket", _ => NewSpecDir(root, "dshmarket", "9.9.9"), NormalizeToRegistry: true) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            Assert.Empty(pending);
            Assert.Contains(logs, l => l.Contains("dshmarket") && l.Contains("已为 registry 安装") && l.Contains("交由市场自管"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnreadableInstalledSpec_ConservativeHandOff()
    {
        var root = NewRoot();
        try
        {
            // dependencies 值非字符串（损坏态）→ spec 形态不可读 → 保守不碰（不回拉 file:）。
            // IsBundleInstalled 仍为 true（键存在），所以走的是放手而非重装分支——行为钉子。
            var dir = Path.Combine(root, "profile");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "package.json"),
                """{"dependencies":{"dshmarket":123},"dsh":{"profile":{"bundles":["dshmarket"]}}}""");
            InstallCopy(dir, "dshmarket", "1.0.0");
            var catalog = new[] { new BundledPluginCatalog.Entry("dshmarket", _ => NewSpecDir(root, "dshmarket", "9.9.9"), NormalizeToRegistry: true) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, dir);

            Assert.Empty(pending);
            Assert.Contains(logs, l => l.Contains("dshmarket") && l.Contains("spec 形态不可读，保守跳过"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LocalInstalled_CurrentBundledVersion_NormalizesToRegistry()
    {
        var root = NewRoot();
        try
        {
            // 本地形态已装且闭包不比已装更新：归化为 registry 自管（spec = 裸包名 → latest）。
            var profile = NewProfile(root, "dshmarket");
            InstallCopy(profile, "dshmarket", "0.0.2");
            var catalog = new[] { new BundledPluginCatalog.Entry("dshmarket", _ => NewSpecDir(root, "dshmarket", "0.0.2"), NormalizeToRegistry: true) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            var entry = Assert.Single(pending);
            Assert.Equal(("dshmarket", "dshmarket@latest", true), (entry.Package, entry.Spec, entry.FromRegistry));
            Assert.Contains(logs, l => l.Contains("随包插件归化") && l.Contains("registry 自管"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LocalInstalled_Older_StillUpgradesViaBundled_NotNormalizedYet()
    {
        var root = NewRoot();
        try
        {
            // 本地形态已装且闭包更高：先走随包 tgz 升级（离线路径），归化留给版本追平后的下次启动。
            var profile = NewProfile(root, "dshmarket");
            InstallCopy(profile, "dshmarket", "0.0.1");
            var bundledDir = NewSpecDir(root, "dshmarket", "0.0.2");
            var catalog = new[] { new BundledPluginCatalog.Entry("dshmarket", _ => bundledDir, NormalizeToRegistry: true) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            var entry = Assert.Single(pending);
            Assert.Equal(("dshmarket", bundledDir, false), (entry.Package, entry.Spec, entry.FromRegistry));
            Assert.Contains(logs, l => l.Contains("随包插件升级"));
            Assert.DoesNotContain(logs, l => l.Contains("归化"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RegistryFallbackResolver_WithLocalInstalled_SkipsUpgradeAndNormalization()
    {
        var root = NewRoot();
        try
        {
            // 闭包无 tgz（解析器回退 registry 串）+ 本地形态已装：版本不可比 → 升级与归化一并
            // 保守放弃（fail-safe，下次闭包带 tgz 即自愈）——负控钉子，防止被当 bug 误修。
            var profile = NewProfile(root, "dshmarket");
            InstallCopy(profile, "dshmarket", "1.0.0");
            var catalog = new[] { new BundledPluginCatalog.Entry("dshmarket", _ => "dshmarket@9.9.9", NormalizeToRegistry: true) };

            var (pending, logs) = Assemble(catalog, UnusedRuntime, profile);

            Assert.Empty(pending);
            Assert.Contains(logs, l => l.Contains("dshmarket") && l.Contains("版本比对失败") && l.Contains("升级与归化检查"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PendingRegistryFlag_DistinguishesSeedFromNormalization()
    {
        var root = NewRoot();
        try
        {
            // 分组契约：随包 tgz 路径 = 本地组（FromRegistry=false）；归化条目 = registry 组（true）。
            // Program.cs 据此分两次 spawn 事务——离线时归化失败不得拖累种子安装。
            var profile = NewProfile(root, "dshmarket");
            InstallCopy(profile, "dshmarket", "1.0.0");
            var bundledDir = NewSpecDir(root, "dshmarket", "1.0.0");
            var catalog = new[]
            {
                new BundledPluginCatalog.Entry("dsh-desktop-companion", _ => "/x/companion.tgz"),
                new BundledPluginCatalog.Entry("dshmarket", _ => bundledDir, NormalizeToRegistry: true),
            };

            var (pending, _) = Assemble(catalog, UnusedRuntime, profile);

            // companion 未装 → 种子条目（本地组）；dshmarket 同版 → 归化条目（registry 组）。
            Assert.Equal(
                [("dsh-desktop-companion", "/x/companion.tgz", false), ("dshmarket", "dshmarket@latest", true)],
                pending.Select(p => (p.Package, p.Spec, p.FromRegistry)).ToList());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PendingOrder_FollowsCatalogOrder()
    {
        var root = NewRoot();
        try
        {
            var profile = NewProfile(root);
            var catalog = new[]
            {
                new BundledPluginCatalog.Entry("second", _ => "/x/second.tgz"),
                new BundledPluginCatalog.Entry("first", _ => "/x/first.tgz"),
            };

            var (pending, _) = Assemble(catalog, UnusedRuntime, profile);

            Assert.Equal(["second", "first"], pending.Select(p => p.Package));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RealCatalog_ResolvesBothBundledPlugins_FromClosureLayout()
    {
        var root = NewRoot();
        try
        {
            // 真实清单 × 真实闭包布局：ResolveMarketSpec 要求 tgz >10K、ResolveCompanionSpec 要求 >1K。
            // 填充条目用随机 Base64——gzip 对高熵内容几乎不压缩，磁盘体积才真能过体积闸。
            TestTarGz.Write(
                Path.Combine(root, "dshmarket.tgz"),
                ("package/package.json", """{"name":"dshmarket","version":"9.9.8"}"""),
                ("package/lib/pad.js", RandomPad(16 * 1024)));
            TestTarGz.Write(
                Path.Combine(root, "dsh-desktop-companion.tgz"),
                ("package/package.json", """{"name":"dsh-desktop-companion","version":"9.9.7"}"""),
                ("package/lib/pad.js", RandomPad(2 * 1024)));

            var profile = NewProfile(root, "dsh-desktop-companion"); // dshmarket 未装、companion 已装旧版
            InstallCopy(profile, "dsh-desktop-companion", "9.9.6");

            var (pending, logs) = Assemble(BundledPluginCatalog.All, root, profile);

            Assert.Equal(
            [
                ("dshmarket", Path.Combine(root, "dshmarket.tgz")),
                ("dsh-desktop-companion", Path.Combine(root, "dsh-desktop-companion.tgz")),
            ], pending.Select(p => (p.Package, p.Spec)));
            Assert.Contains(logs, l => l.Contains("随包插件升级：dsh-desktop-companion 9.9.6 → 9.9.7"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RealCatalog_EmptyClosure_CompanionSkipped_MarketRegistryFallback()
    {
        var root = NewRoot();
        try
        {
            var profile = NewProfile(root);

            var (pending, logs) = Assemble(BundledPluginCatalog.All, root, profile);

            // 开发用 PATH dsh 场景：companion 无闭包来源跳过；dshmarket 保底走 registry spec 仍可首装。
            var entry = Assert.Single(pending);
            Assert.Equal("dshmarket", entry.Package);
            Assert.StartsWith("dshmarket@", entry.Spec);
            Assert.Contains(logs, l => l.Contains("dsh-desktop-companion") && l.Contains("本闭包未携带"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>随机 Base64 填充串：高熵不可压缩，保证 tgz 磁盘体积达到闸值。</summary>
    private static string RandomPad(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes)[..length];
    }
}
