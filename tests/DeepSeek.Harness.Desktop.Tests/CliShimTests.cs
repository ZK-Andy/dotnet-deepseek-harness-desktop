using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>CLI shim 相关测试集合必须真串行：会改写进程级 DSH_DESKTOP_CLI_* 环境变量并行会互相污染。</summary>
[CollectionDefinition("cli-shim-env", DisableParallelization = true)]
public class CliShimEnvCollectionDefinition;

/// <summary>CLI shim 内容生成契约：dsh 已全局在 PATH（ADR simple-shell-single-global-dsh），只保留 pnpm shim 的转发语义。</summary>
[Collection("cli-shim-env")]
public class CliShimBuilderTests
{
    // pnpm shim 内容（不烘焙 home/runtime；只转发用户 pnpm）

    /// <summary>验证 pnpm 三类 shim 只转发用户 pnpm：不烘焙 home/runtime，缺失时输出「pnpm not found」。</summary>
    [Fact]
    public void PnpmShims_Do_Not_Bake_Home_Or_Runtime()
    {
        Assert.DoesNotContain("/home/test/.dsh", CliShimBuilder.BuildPnpmSh());
        Assert.DoesNotContain("runtime", CliShimBuilder.BuildPnpmSh());
        Assert.Contains("pnpm command shim (generated)", CliShimBuilder.BuildPnpmSh());
        Assert.Contains("pnpm not found", CliShimBuilder.BuildPnpmSh());
        Assert.Contains("pnpm not found", CliShimBuilder.BuildPnpmCmd());
        Assert.Contains("pnpm not found", CliShimBuilder.BuildPnpmPs1());
        Assert.Contains(CliShimBuilder.GeneratedMarker, CliShimBuilder.BuildPnpmSh());
        Assert.Contains(CliShimBuilder.GeneratedMarker, CliShimBuilder.BuildPnpmCmd());
    }

    /// <summary>验证 pnpm shim 不含 dsh 相关烘焙（dsh 全局在 PATH，无需 shim）。</summary>
    [Fact]
    public void PnpmShims_Do_Not_Bake_Dsh()
    {
        Assert.DoesNotContain("dsh", CliShimBuilder.BuildPnpmSh(), StringComparison.Ordinal);
        Assert.DoesNotContain("DSH_HOME", CliShimBuilder.BuildPnpmSh());
        Assert.DoesNotContain("bin.js", CliShimBuilder.BuildPnpmSh());
    }

    /// <summary>验证三类 pnpm shim 在前两行内带生成标记（可被 IsGeneratedShim 识别为"本应用生成"）。</summary>
    [Fact]
    public void PnpmShims_All_Carry_GeneratedMarker()
    {
        Assert.True(CliShimPath.IsGeneratedShim(CliShimBuilder.BuildPnpmCmd()));
        Assert.True(CliShimPath.IsGeneratedShim(CliShimBuilder.BuildPnpmPs1()));
        Assert.True(CliShimPath.IsGeneratedShim(CliShimBuilder.BuildPnpmSh()));
    }
}

/// <summary>shim 路径工具函数契约：PATH token 的大小写不敏感合并与判含、shell rc 块的幂等插入与生成标记识别。</summary>
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
        string block = CliShimPath.BuildShellExportBlock("/home/x/.local/bin", ":");
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
        string block = CliShimPath.BuildShellExportBlock("/home/x/.local/bin", ":");
        string result = CliShimPath.EnsureShellRcBlock("# existing\n", block);
        Assert.StartsWith("# existing", result);
        Assert.Contains(CliShimPath.RcBeginMarker, result);
    }

    /// <summary>验证提供 pnpm store/cache 目录时，rc 块同时导出两者（ADR pnpm-store-alignment-with-terminal）。</summary>
    [Fact]
    public void BuildShellExportBlocks_WithPnpmDirs_ExportsStoreAndCache()
    {
        string block = CliShimPath.BuildShellExportBlocks(
            new[] { "/home/x/.local/bin" },
            ":",
            pnpmStoreDir: "/home/x/.dsh/.pnpm-store",
            pnpmCacheDir: "/home/x/.dsh/.pnpm-cache");
        Assert.Contains("export pnpm_config_store_dir=\"/home/x/.dsh/.pnpm-store\"", block);
        Assert.Contains("export pnpm_config_cache_dir=\"/home/x/.dsh/.pnpm-cache\"", block);
    }

    /// <summary>验证未提供 pnpm 目录时 rc 块保持原 PATH 语义，不产生多余的 store 空行。</summary>
    [Fact]
    public void BuildShellExportBlocks_WithoutPnpmDirs_KeepsPathOnly()
    {
        string withStore = CliShimPath.BuildShellExportBlocks(new[] { "/home/x/.local/bin" }, ":", "/home/x/.dsh/.pnpm-store", "/home/x/.dsh/.pnpm-cache");
        string withoutStore = CliShimPath.BuildShellExportBlocks(new[] { "/home/x/.local/bin" }, ":");
        Assert.DoesNotContain("pnpm_config_store_dir", withoutStore);
        Assert.DoesNotContain("pnpm_config_cache_dir", withoutStore);
        Assert.Contains("export PATH=", withoutStore);
        Assert.Contains("pnpm_config_store_dir", withStore);
    }

    /// <summary>验证旧版本桌面块（只含 PATH，无 pnpm export）被升级补进 pnpm store/cache 行（ADR pnpm-store-alignment-with-terminal）。</summary>
    [Fact]
    public void EnsurePnpmDirsInRc_Upgrades_OldBlock_WithoutPnpm()
    {
        string oldBlock = CliShimPath.BuildShellExportBlocks(new[] { "/home/x/.local/bin" }, ":");
        string upgraded = CliShimPath.EnsurePnpmDirsInRc(oldBlock, "/home/x/.dsh/.pnpm-store", "/home/x/.dsh/.pnpm-cache");
        Assert.Contains("export pnpm_config_store_dir=\"/home/x/.dsh/.pnpm-store\"", upgraded);
        Assert.Contains("export pnpm_config_cache_dir=\"/home/x/.dsh/.pnpm-cache\"", upgraded);
        Assert.Contains("export PATH=", upgraded);
        // 块标记仍在，且 PATH 行保留
        Assert.Contains(CliShimPath.RcBeginMarker, upgraded);
        Assert.Contains(CliShimPath.RcEndMarker, upgraded);
    }

    /// <summary>验证 rc 整文件已含 pnpm export（无论块内块外）时再升级为幂等（原样返回），不重复追加。</summary>
    [Fact]
    public void EnsurePnpmDirsInRc_Is_Idempotent_When_Pnpm_Present_Anywhere()
    {
        string block = CliShimPath.BuildShellExportBlocks(new[] { "/home/x/.local/bin" }, ":", "/home/x/.dsh/.pnpm-store", "/home/x/.dsh/.pnpm-cache");
        Assert.Equal(block, CliShimPath.EnsurePnpmDirsInRc(block, "/home/x/.dsh/.pnpm-store", "/home/x/.dsh/.pnpm-cache"));

        // pnpm 行在桌面块之外（用户手改场景）：同样视为已含，不重复追加
        string outsideBlock = block + "\n# user lines\nexport pnpm_config_store_dir=\"/home/x/.dsh/.pnpm-store\"\n";
        string onceMore = CliShimPath.EnsurePnpmDirsInRc(outsideBlock, "/home/x/.dsh/.pnpm-store", "/home/x/.dsh/.pnpm-cache");
        Assert.Contains("export PATH=", onceMore);
        Assert.Contains("pnpm_config_store_dir", onceMore);
    }

    /// <summary>验证 rc 无桌面块（无标记）时升级函数原样返回（整块追加交由 EnsureShellRcBlock）。</summary>
    [Fact]
    public void EnsurePnpmDirsInRc_No_Op_When_No_Block()
    {
        const string plain = "# user rc\n";
        Assert.Equal(plain, CliShimPath.EnsurePnpmDirsInRc(plain, "/home/x/.dsh/.pnpm-store", "/home/x/.dsh/.pnpm-cache"));
    }

    /// <summary>验证仅前两行含生成标记的脚本才被识别为本应用 shim，正文提及标记或 null 输入均不算。</summary>
    [Fact]
    public void IsGeneratedShim_Only_Marks_Marker_In_Header()
    {
        // 前两行含生成标记 → 本应用 shim
        Assert.True(CliShimPath.IsGeneratedShim("#!/bin/sh\n# DeepSeek Harness Desktop - pnpm command shim\n"));
        // 正文（第三行）才提到标记 → 用户文件
        Assert.False(CliShimPath.IsGeneratedShim("#!/bin/sh\necho user\necho DeepSeek Harness Desktop - note\n"));
        Assert.False(CliShimPath.IsGeneratedShim(null));
    }
}

/// <summary>安装计划契约：按平台产出 pnpm 文件集、PATH 分隔符与 shell rc 块（dsh 已全局，不产出 dsh shim），以及写入决策矩阵。</summary>
[Collection("cli-shim-env")]
public class CliShimPlannerTests
{
    private const string Bin = "/home/test/.local/bin";

    /// <summary>验证 Windows 安装计划仅产出 pnpm 的 cmd+ps1 两文件、分号 PATH 分隔且无 shell rc 块。</summary>
    [Fact]
    public void Windows_Setup_Emits_Only_Pnpm_Cmd_Ps1_And_Semicolon_Sep()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: true);
        Assert.Equal(2, setup.Files.Count);
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.cmd"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.ps1"));
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.Contains("dsh", StringComparison.Ordinal));
        Assert.Equal(";", setup.PathSeparator);
        Assert.Null(setup.ShellRcBlock);
    }

    /// <summary>验证 Unix 安装计划仅产出可执行 pnpm、冒号 PATH 分隔并带包含 bin 目录的 shell rc 块。</summary>
    [Fact]
    public void Unix_Setup_Emits_Only_Pnpm_And_Sh_Block()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: false);
        Assert.Single(setup.Files);
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm") && f.Executable);
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.Contains("dsh", StringComparison.Ordinal));
        Assert.Equal(":", setup.PathSeparator);
        Assert.NotNull(setup.ShellRcBlock);
        Assert.Contains(Bin, setup.ShellRcBlock);
    }

    /// <summary>验证 Windows 计划使用 pnpm 常量文件名（cmd/ps1）。</summary>
    [Fact]
    public void Windows_Files_Match_Pnpm_Constants()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: true);
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith(CliShimPlanner.PnpmCmdName));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith(CliShimPlanner.PnpmPs1Name));
    }

    /// <summary>验证 Unix 计划使用 pnpm 常量文件名（无扩展名、可执行）。</summary>
    [Fact]
    public void Unix_File_Matches_Pnpm_Constant()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: false);
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith(CliShimPlanner.PnpmShName) && f.Executable);
    }

    /// <summary>验证 Unix 计划在提供系统全局 node 时（无系统 node 场景）把其 global bin 目录一并暴露进 shell rc 块。</summary>
    [Fact]
    public void Unix_Setup_ExposesSystemGlobalNodeBinDir()
    {
        const string nodeBin = "/usr/local/bin";
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: false, runtimeNodeBinDir: nodeBin);
        Assert.Equal(nodeBin, setup.RuntimeNodeBinDir);
        Assert.Contains(nodeBin, setup.ShellRcBlock);
        Assert.Contains(Bin, setup.ShellRcBlock);
        Assert.True(setup.ShellRcBlock!.IndexOf(nodeBin, StringComparison.Ordinal) < setup.ShellRcBlock.IndexOf(Bin, StringComparison.Ordinal),
            "系统全局 node 目录应排在 PATH 前面（终端优先用桌面与终端共用那份 node/dsh）");
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

    /// <summary>验证 Unix 计划在提供 pnpm store/cache 目录时记录二者并写入 shell rc 块（ADR pnpm-store-alignment-with-terminal）。</summary>
    [Fact]
    public void Unix_Setup_RecordsAndExportsPnpmDirs()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: false, pnpmStoreDir: "/home/test/.dsh/.pnpm-store", pnpmCacheDir: "/home/test/.dsh/.pnpm-cache");
        Assert.Equal("/home/test/.dsh/.pnpm-store", setup.PnpmStoreDir);
        Assert.Equal("/home/test/.dsh/.pnpm-cache", setup.PnpmCacheDir);
        Assert.Contains("export pnpm_config_store_dir=\"/home/test/.dsh/.pnpm-store\"", setup.ShellRcBlock);
        Assert.Contains("export pnpm_config_cache_dir=\"/home/test/.dsh/.pnpm-cache\"", setup.ShellRcBlock);
    }

    /// <summary>验证 Windows 计划不导出 pnpm store（Windows 走 HKCU 注册表 PATH，无 shell rc 块）。</summary>
    [Fact]
    public void Windows_Setup_DoesNotExportPnpmDirs()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Bin, isWindows: true, pnpmStoreDir: @"C:\Users\x\.dsh\.pnpm-store", pnpmCacheDir: @"C:\Users\x\.dsh\.pnpm-cache");
        Assert.Null(setup.ShellRcBlock);
    }

    /// <summary>验证目标已存在且非本应用 shim 时写入决策为保留用户文件。</summary>
    [Fact]
    public void DecideShimWrite_Preserves_User_File()
    {
        ShimWriteAction action = CliShimPlanner.DecideShimWrite(exists: true, isGeneratedShim: false, isSymlink: false);
        Assert.Equal(ShimWriteAction.PreserveUserFile, action);
    }

    /// <summary>验证目标缺失或已是本应用生成 shim 时写入决策为覆盖。</summary>
    [Fact]
    public void DecideShimWrite_Writes_Generated_Or_Missing()
    {
        Assert.Equal(ShimWriteAction.Write, CliShimPlanner.DecideShimWrite(exists: false, isGeneratedShim: false, isSymlink: false));
        Assert.Equal(ShimWriteAction.Write, CliShimPlanner.DecideShimWrite(exists: true, isGeneratedShim: true, isSymlink: false));
    }

    /// <summary>验证目标为悬空符号链接时写入决策为先移除链接再写入。</summary>
    [Fact]
    public void DecideShimWrite_Removes_Dangling_Symlink()
    {
        ShimWriteAction action = CliShimPlanner.DecideShimWrite(exists: false, isGeneratedShim: false, isSymlink: true);
        Assert.Equal(ShimWriteAction.RemoveDanglingSymlinkThenWrite, action);
    }
}

/// <summary>端到端注册契约：TryRegister 落盘 pnpm shim 与 rc 块、重复注册幂等、保留用户既有文件（dsh 已全局，不写 dsh shim）；
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
        try { Directory.Delete(Path.GetDirectoryName(_binDir)!, recursive: true); } catch { }
    }

    private bool IsWindows() => OperatingSystem.IsWindows();

    /// <summary>验证 TryRegister 落盘 pnpm shim（含生成标记）且不落盘 dsh shim，注册返回成功。</summary>
    [Fact]
    public void TryRegister_Writes_Pnpm_Shim_Not_Dsh()
    {
        var reg = new CliShimRegistrar(_ => { });
        bool ok = reg.TryRegister();

        Assert.True(ok);
        string pnpmShim = Path.Combine(_binDir, IsWindows() ? "pnpm.cmd" : "pnpm");
        Assert.True(File.Exists(pnpmShim), "pnpm shim written");
        Assert.Contains(CliShimBuilder.GeneratedMarker, File.ReadAllText(pnpmShim));
        // dsh 已全局在 PATH：不产出 dsh shim
        string dshShim = Path.Combine(_binDir, IsWindows() ? "dsh.cmd" : "dsh");
        Assert.False(File.Exists(dshShim), "dsh shim must not be written (global dsh)");
    }

    /// <summary>验证重复注册两次不会在 shell rc 中重复插入 rc 块标记。</summary>
    [Fact]
    public void TryRegister_Is_Idempotent_For_Rc()
    {
        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister();
        reg.TryRegister();

        string rc = File.ReadAllText(Path.Combine(_rcHome, ".bashrc"));
        Assert.Equal(1, Count(rc, CliShimPath.RcBeginMarker));
    }

    /// <summary>验证 TryRegister 把 pnpm store/cache 目录导出进 shell rc，终端与桌面共用同一份 pnpm store（ADR pnpm-store-alignment-with-terminal）。
    /// 仅 Unix：Windows 走 HKCU 注册表 PATH、不写 shell rc（与既有 rc 类测试一致）。</summary>
    [Fact]
    public void TryRegister_ExportsPnpmStoreToShellRc()
    {
        if (IsWindows())
        {
            return; // Windows 不写 shell rc（走注册表），该契约仅 Unix 生效
        }

        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister();

        string rc = File.ReadAllText(Path.Combine(_rcHome, ".bashrc"));
        string store = Path.Combine(_dshHome, ".pnpm-store");
        string cache = Path.Combine(_dshHome, ".pnpm-cache");
        Assert.Contains($"export pnpm_config_store_dir=\"{store}\"", rc);
        Assert.Contains($"export pnpm_config_cache_dir=\"{cache}\"", rc);
    }

    /// <summary>验证目标 shim 已被用户文件占据时注册不改写其内容。</summary>
    [Fact]
    public void TryRegister_Preserves_User_Shim_File()
    {
        string pnpmShim = Path.Combine(_binDir, IsWindows() ? "pnpm.cmd" : "pnpm");
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(pnpmShim, "#!/bin/sh\necho my real pnpm\n");

        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister();

        Assert.Equal("#!/bin/sh\necho my real pnpm\n", File.ReadAllText(pnpmShim));
    }

    /// <summary>验证提供系统全局 node（无系统 node 场景）时 TryRegister 把其 global bin 目录写入 shell rc，终端可解析同一份 node/dsh。</summary>
    [Fact]
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
