
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.CliShim;

/// <summary>CLI shim 相关测试集合必须真串行：会改写进程级 DSH_DESKTOP_CLI_* 环境变量并行会互相污染。</summary>
[CollectionDefinition("cli-shim-env", DisableParallelization = true)]
public class CliShimEnvCollectionDefinition;

/// <summary>shim 路径工具函数契约：PATH token 的大小写不敏感合并与判含、shell rc 块的幂等插入、
/// 生成标记识别（清理历史自产残留的判定面——ADR companion-pnpm-prerequisite；原 Builder 契约类
/// 随 CliShimBuilder 撤除后并入此处，R1 S3）。</summary>
[Collection("cli-shim-env")]
public class CliShimPathTests
{
    /// <summary>验证 PATH token 合并大小写不敏感且幂等：已含同 token 保持原样，未含时追加到末尾。</summary>
    [Fact]
    public void MergePathToken_Is_Idempotent_CaseInsensitive()
    {
        const string sep = ";";
        string merged = CliShimPath.MergePathToken(@"C:\Users\x\bin", @"C:\USERS\X\BIN", sep, caseInsensitive: true);
        Assert.Equal(@"C:\Users\x\bin", merged);
        // 未含 token 时追加
        Assert.Equal(
            @"C:\A;C:\B;C:\C",
            CliShimPath.MergePathToken(@"C:\A;C:\B", @"C:\C", sep, caseInsensitive: true));
    }

    /// <summary>验证 Windows PATH 判含忽略条目尾部反斜杠差异（C:\B\ 命中 C:\B）。</summary>
    [Fact]
    public void PathContainsToken_Normalizes_Win_Trailing_Backslash()
    {
        Assert.True(CliShimPath.PathContainsToken(@"C:\A;C:\B\", @"C:\B", ";", caseInsensitive: true));
        Assert.False(CliShimPath.PathContainsToken(@"C:\A;C:\B", @"C:\C", ";", caseInsensitive: true));
    }

    /// <summary>验证空 PATH 合并时直接返回 token 本身。</summary>
    [Fact]
    public void MergePathToken_Empty_Path_Returns_Token()
    {
        string merged = CliShimPath.MergePathToken("", "/home/x/bin", ":", caseInsensitive: false);
        Assert.Equal("/home/x/bin", merged);
    }

    /// <summary>验证 Unix PATH 判含忽略条目尾部斜杠差异（/b/ 命中 /b）。</summary>
    [Fact]
    public void Unix_PathContainsToken_Normalizes_Trailing_Slash()
    {
        Assert.True(CliShimPath.PathContainsToken("/a:/b", "/b/", ":", caseInsensitive: false));
        Assert.False(CliShimPath.PathContainsToken("/a:/b", "/c", ":", caseInsensitive: false));
    }

    /// <summary>验证 rc 块插入幂等：已含标记的 rc 再次插入后逐字符不变。</summary>
    [Fact]
    public void EnsureShellRcBlock_Is_Idempotent()
    {
        string block = CliShimPath.BuildShellExportBlocks(new[] { "/home/x/.local/bin" }, ":");
        string once = CliShimPath.EnsureShellRcBlock("", block);
        Assert.Contains(CliShimPath.RcBeginMarker, once);
        Assert.Contains(CliShimPath.RcEndMarker, once);

        string twice = CliShimPath.EnsureShellRcBlock(once, block);
        Assert.Equal(once, twice);
    }

    /// <summary>验证 rc 块追加在既有内容之后且不覆盖原内容。</summary>
    [Fact]
    public void EnsureShellRcBlock_Appends_After_Existing_Content()
    {
        string block = CliShimPath.BuildShellExportBlocks(new[] { "/home/x/.local/bin" }, ":");
        string result = CliShimPath.EnsureShellRcBlock("# existing\n", block);
        Assert.StartsWith("# existing", result);
        Assert.Contains(CliShimPath.RcBeginMarker, result);
    }

    /// <summary>验证仅前两行含生成标记的脚本才被识别为本应用 shim，正文提及标记或 null 输入均不算。</summary>
    [Fact]
    public void IsGeneratedShim_Only_Marks_Marker_In_Header()
    {
        // 前两行含生成标记 → 本应用 shim（常量耦合断言：标记与实现同源，改字面即红）
        Assert.True(CliShimPath.IsGeneratedShim($"#!/bin/sh\n# {CliShimPath.GeneratedMarker} pnpm command shim\n"));
        // 正文（第三行起）才提到标记 → 用户文件——清理是 File.Delete 破坏性动作，该分支必须钉住
        Assert.False(CliShimPath.IsGeneratedShim("#!/bin/sh\necho user\necho DeepSeek Harness Desktop - note\n"));
        Assert.False(CliShimPath.IsGeneratedShim(null));
    }
}

/// <summary>安装计划契约（ADR companion-pnpm-prerequisite）：pnpm 转发 shim 已停写——规划只含
/// 历史残留清理清单与系统全局 node bin 的 PATH 暴露（不再产出任何 shim 文件、binDir 不进 PATH）。</summary>
[Collection("cli-shim-env")]
public class CliShimPlannerTests
{
    private const string Bin = "/home/test/.local/bin";

    /// <summary>验证规划不再产出任何 shim 文件面：只有历史残留清理清单，含三平台 pnpm 全名域。</summary>
    [Fact]
    public void Setup_Plans_Legacy_Cleanup_Only_No_New_Shims()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: false, runtimeNodeBinDir: null);
        Assert.Equal(3, setup.LegacyShimPaths.Count);
        foreach (string name in CliShimPlanner.LegacyPnpmShimNames)
        {
            Assert.Contains(Path.Combine(Bin, name), setup.LegacyShimPaths);
        }
    }

    /// <summary>验证 Unix 无系统全局 node 可暴露时无 rc 块（binDir 不再进 PATH——shim 已撤，
    /// ~/.local/bin 由用户自管）。</summary>
    [Fact]
    public void Unix_Setup_NoNodeBin_NoRcBlock()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: false, runtimeNodeBinDir: null);
        Assert.Null(setup.ShellRcBlock);
    }

    /// <summary>验证 Unix 规划在提供系统全局 node 时 rc 块只含 node bin（binDir 不进 PATH）。</summary>
    [Fact]
    public void Unix_Setup_ExposesSystemGlobalNodeBinDir_Only()
    {
        const string nodeBin = "/usr/local/bin";
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: false, runtimeNodeBinDir: nodeBin);
        Assert.Equal(nodeBin, setup.RuntimeNodeBinDir);
        Assert.NotNull(setup.ShellRcBlock);
        Assert.Contains(nodeBin, setup.ShellRcBlock);
        Assert.DoesNotContain(Bin, setup.ShellRcBlock!);
    }

    /// <summary>验证 Windows 计划在提供系统全局 node 时记录 RuntimeNodeBinDir（供 HKCU Path 合并）。</summary>
    [Fact]
    public void Windows_Setup_RecordsSystemGlobalNodeBinDir()
    {
        const string nodeBin = @"C:\Program Files\nodejs";
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: true, runtimeNodeBinDir: nodeBin);
        Assert.Equal(nodeBin, setup.RuntimeNodeBinDir);
        Assert.Null(setup.ShellRcBlock);
    }
}

/// <summary>端到端注册契约：TryRegister 清理历史自产 pnpm shim 残留（不写新 shim）、node bin 暴露 rc 块幂等、保留用户自有文件；
/// 夹具接管并还原进程级 DSH_DESKTOP_CLI_* 环境变量。</summary>
[Collection("cli-shim-env")]
public class CliShimRegistrarTests : IDisposable
{
    private readonly string _binDir;
    private readonly string _rcHome;
    private readonly string _dshHome;
    private readonly string? _oldBinDir;
    private readonly string? _oldRcHome;
    private readonly string? _oldDshHome;

    /// <summary>初始化隔离的 CLI shim 测试环境：临时 bin/rc/dsh-home 目录 + 注入 DSH_DESKTOP_CLI_BIN_DIR /
    /// DSH_DESKTOP_CLI_RC_HOME / DSH_DESKTOP_DSH_HOME，结束时清理。</summary>
    public CliShimRegistrarTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "dsh-cli-shim-" + Guid.NewGuid().ToString("N"));
        _binDir = Path.Combine(root, "bin");
        _rcHome = Path.Combine(root, "rc");
        _dshHome = Path.Combine(root, "dsh");
        Directory.CreateDirectory(_rcHome);
        File.WriteAllText(Path.Combine(_rcHome, ".bashrc"), "# existing bashrc\n");

        _oldBinDir = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR");
        _oldRcHome = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME");
        _oldDshHome = Environment.GetEnvironmentVariable("DSH_DESKTOP_DSH_HOME");
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR", _binDir);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME", _rcHome);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", _dshHome);
    }

    /// <summary>还原 DSH_DESKTOP_CLI_* / DSH_DESKTOP_DSH_HOME 环境变量并递归删除本夹具的临时目录。</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR", _oldBinDir);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME", _oldRcHome);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_DSH_HOME", _oldDshHome);
        try { Directory.Delete(Path.GetDirectoryName(_binDir)!, recursive: true); } catch (Exception) { /* 夹具目录清理失败不影响断言 */ }
    }

    private bool IsWindows() => OperatingSystem.IsWindows();

    /// <summary>验证 TryRegister 不再写任何新 shim，且历史自产 pnpm 转发 shim（带生成标记）被清理。</summary>
    [UnixFact("Unix-only 语义：Windows 的 RegisterPath 直写 HKCU 环境变量且无测试注入 seam，跑用例会永久污染用户 PATH")]
    public void TryRegister_Removes_Legacy_Generated_Shim_No_New_Writes()
    {
        Directory.CreateDirectory(_binDir);
        string pnpmShim = Path.Combine(_binDir, IsWindows() ? "pnpm.cmd" : "pnpm");
        File.WriteAllText(pnpmShim, $"#!/bin/sh\n# {CliShimPath.GeneratedMarker} pnpm command shim (generated)\nexit 1\n");

        var reg = new CliShimRegistrar(_ => { });
        bool ok = reg.TryRegister();

        Assert.True(ok);
        Assert.False(File.Exists(pnpmShim), "legacy generated pnpm shim must be removed");
        Assert.Empty(Directory.GetFiles(_binDir));
    }

    /// <summary>验证重复注册两次不会在 shell rc 中重复插入 rc 块标记（node bin 暴露形态）。</summary>
    [UnixFact("Unix-only 语义：Windows 的 RegisterPath 直写 HKCU 环境变量，无测试注入 seam")]
    public void TryRegister_Is_Idempotent_For_Rc()
    {
        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister("/home/user/.local/bin");
        reg.TryRegister("/home/user/.local/bin");

        string rc = File.ReadAllText(Path.Combine(_rcHome, ".bashrc"));
        Assert.Equal(1, Count(rc, CliShimPath.RcBeginMarker));
    }

    /// <summary>验证用户自放的 pnpm（真 pnpm，无生成标记）不被清理——只删自产残留。</summary>
    [UnixFact("Unix-only 语义：Windows 的 RegisterPath 直写 HKCU 环境变量且无测试注入 seam")]
    public void TryRegister_Preserves_User_Pnpm_File()
    {
        Directory.CreateDirectory(_binDir);
        string userPnpm = Path.Combine(_binDir, IsWindows() ? "pnpm.cmd" : "pnpm");
        File.WriteAllText(userPnpm, "#!/bin/sh\necho my real pnpm\n");

        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister();

        Assert.Equal("#!/bin/sh\necho my real pnpm\n", File.ReadAllText(userPnpm));
    }

    /// <summary>验证提供系统全局 node（无系统 node 场景）时 TryRegister 把其 global bin 目录写入 shell rc，终端可解析同一份 node/dsh。</summary>
    [UnixFact("Unix-only 语义：Windows 的 RegisterPath 直写 HKCU 环境变量，无测试注入 seam")]
    public void TryRegister_ExposesSystemGlobalNodeBinDir()
    {
        const string nodeBin = "/home/user/.local/bin";
        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister(nodeBin);

        string rc = File.ReadAllText(Path.Combine(_rcHome, ".bashrc"));
        Assert.Contains(nodeBin, rc);
        Assert.Contains(CliShimPath.RcBeginMarker, rc);
        Assert.DoesNotContain("dsh command shim", rc);
    }

    private static int Count(string text, string needle)
    {
        int c = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            c++;
        }
        return c;
    }
}
