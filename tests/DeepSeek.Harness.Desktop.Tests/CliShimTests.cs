using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>CLI shim 相关测试集合必须真串行：会改写进程级 DSH_DESKTOP_CLI_* 环境变量并行会互相污染。</summary>
[CollectionDefinition("cli-shim-env", DisableParallelization = true)]
public class CliShimEnvCollectionDefinition;

/// <summary>CLI shim 内容生成契约：cmd/ps1/sh 三类 shim 的转义规则、烘焙的运行时与 DSH_HOME、用户 dsh 优先顺序。</summary>
[Collection("cli-shim-env")]
public class CliShimBuilderTests
{
    private const string Runtime = "/home/test/.dsh-desktop/runtime";
    private const string DshHome = "/home/test/.dsh";

    // ------------------------------------------------------------------
    // 转义
    // ------------------------------------------------------------------

    /// <summary>验证 cmd 转义将 % 加倍为 %%，防止批处理变量在 set 赋值前被展开。</summary>
    [Fact]
    public void EscapeCmd_Doubles_Percent()
    {
        Assert.Equal(@"C:\Users\100%%test", CliShimBuilder.EscapeCmd(@"C:\Users\100%test"));
    }

    /// <summary>验证 PowerShell 转义将单引号加倍（''），使路径在单引号字符串中字面成立。</summary>
    [Fact]
    public void EscapePs1_Doubles_SingleQuote()
    {
        Assert.Equal(@"C:\Users\o''brien", CliShimBuilder.EscapePs1(@"C:\Users\o'brien"));
    }

    /// <summary>验证 POSIX sh 转义用 '"'"' 序列表达单引号，路径可安全嵌入 sh 脚本。</summary>
    [Fact]
    public void EscapeSh_Escapes_SingleQuote()
    {
        Assert.Equal(@"/home/o'\''brien", CliShimBuilder.EscapeSh(@"/home/o'brien"));
    }

    // ------------------------------------------------------------------
    // dsh shim 内容
    // ------------------------------------------------------------------

    /// <summary>验证 dsh.cmd 烘焙运行时绝对路径、DSH_HOME、DSH_TELEMETRY_DISABLED=1 与 %* 参数透传。</summary>
    [Fact]
    public void DshCmd_Bakes_Runtime_And_Home()
    {
        string content = CliShimBuilder.BuildDshCmd(Runtime, DshHome);
        Assert.Contains("dsh command shim (generated)", content);
        Assert.Contains(@"/home/test/.dsh-desktop/runtime", content);
        Assert.Contains("/home/test/.dsh", content);
        Assert.Contains(@"node_modules\@deepseek-ai\dsh\lib\bin.js", content);
        Assert.Contains("DSH_TELEMETRY_DISABLED=1", content);
        Assert.Contains("%*", content);
    }

    /// <summary>验证烘焙路径中的 % 被加倍转义，set RUNTIME_DIR 赋值不会误展开环境变量。</summary>
    [Fact]
    public void DshCmd_Escapes_Percent_In_Baked_Path()
    {
        string content = CliShimBuilder.BuildDshCmd(@"C:\Users\100%test\runtime", DshHome);
        Assert.Contains(@"100%%test", content);
        Assert.DoesNotContain(@"set ""RUNTIME_DIR=C:\Users\100%test\runtime""", content);
    }

    /// <summary>验证用户已装 dsh（call "%USER_DSH%"）的早退分支位于随包 node 启动之前。</summary>
    [Fact]
    public void DshCmd_Prefers_User_Dsh_Before_Runtime_Launch()
    {
        string content = CliShimBuilder.BuildDshCmd(Runtime, DshHome);
        int userAt = content.IndexOf("USER_DSH", StringComparison.Ordinal);
        int launchAt = content.IndexOf(@"""%NODE%""", StringComparison.Ordinal);
        Assert.True(userAt >= 0 && launchAt > 0 && userAt < launchAt, "user dsh precedence must precede bundled launch");
    }

    /// <summary>验证 DSH_HOME 与遥测禁用仅在用户 dsh 早退之后注入，不污染用户自身环境。</summary>
    [Fact]
    public void DshCmd_Injects_DshHome_Only_In_Bundled_Path()
    {
        // DSH_HOME/telemetry 必须在用户 dsh 早退（call "%USER_DSH%"）之后注入，避免污染用户自己的 dsh 环境
        string content = CliShimBuilder.BuildDshCmd(Runtime, DshHome);
        int userExitAt = content.IndexOf("call \"%USER_DSH%\"", StringComparison.Ordinal);
        int homeAt = content.IndexOf("set \"DSH_HOME=", StringComparison.Ordinal);
        int telemetryAt = content.IndexOf("set \"DSH_TELEMETRY_DISABLED=1\"", StringComparison.Ordinal);
        Assert.True(userExitAt >= 0 && homeAt > userExitAt && telemetryAt > userExitAt,
            "DSH_HOME/telemetry must be injected only after the user-dsh early-exit (bundled path)");
    }

    /// <summary>验证 dsh.ps1 烘焙路径、优先调用用户 Get-Command dsh，且 DSH_HOME 注入位于用户分支之后。</summary>
    [Fact]
    public void DshPs1_Bakes_And_Prefers_User_Dsh()
    {
        string content = CliShimBuilder.BuildDshPs1(Runtime, DshHome);
        Assert.Contains(Runtime, content);
        Assert.Contains(DshHome, content);
        Assert.Contains("Get-Command dsh -All", content);
        int userAt = content.IndexOf("$userDsh", StringComparison.Ordinal);
        int dshHomeAt = content.IndexOf("$env:DSH_HOME = $dshHome", StringComparison.Ordinal);
        Assert.True(userAt >= 0 && dshHomeAt >= 0 && userAt < dshHomeAt,
            "user dsh precedence must precede DSH_HOME injection (bundled-only)");
    }

    /// <summary>验证 dsh sh 烘焙路径、优先 exec 用户 $dir/dsh，且 DSH_HOME export 位于用户分支之后。</summary>
    [Fact]
    public void DshSh_Bakes_And_Prefers_User_Dsh()
    {
        string content = CliShimBuilder.BuildDshSh(Runtime, DshHome);
        Assert.Contains(Runtime, content);
        Assert.Contains(DshHome, content);
        Assert.Contains(@"exec ""$dir/dsh"" ""$@""", content);
        int userAt = content.IndexOf(@"""$dir/dsh""", StringComparison.Ordinal);
        int homeAt = content.IndexOf("export DSH_HOME", StringComparison.Ordinal);
        Assert.True(userAt >= 0 && homeAt >= 0 && userAt < homeAt,
            "user dsh precedence must precede DSH_HOME export");
    }

    // ------------------------------------------------------------------
    // pnpm shim 内容（不烘焙 home/runtime；只转发用户 pnpm）
    // ------------------------------------------------------------------

    /// <summary>验证 pnpm 三类 shim 只转发用户 pnpm：不烘焙 home/runtime，缺失时输出「pnpm not found」。</summary>
    [Fact]
    public void PnpmShims_Do_Not_Bake_Home_Or_Runtime()
    {
        Assert.DoesNotContain(Runtime, CliShimBuilder.BuildPnpmSh());
        Assert.DoesNotContain(DshHome, CliShimBuilder.BuildPnpmSh());
        Assert.Contains("pnpm command shim (generated)", CliShimBuilder.BuildPnpmSh());
        Assert.Contains("pnpm not found", CliShimBuilder.BuildPnpmSh());
        Assert.Contains("pnpm not found", CliShimBuilder.BuildPnpmCmd());
        Assert.Contains("pnpm not found", CliShimBuilder.BuildPnpmPs1());
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

    /// <summary>验证仅前两行含生成标记的脚本才被识别为本应用 shim，正文提及标记或 null 输入均不算。</summary>
    [Fact]
    public void IsGeneratedShim_Only_Marks_Marker_In_Header()
    {
        // 前两行含生成标记 → 本应用 shim
        Assert.True(CliShimPath.IsGeneratedShim("#!/bin/sh\n# DeepSeek Harness Desktop - dsh command shim\n"));
        // 正文（第三行）才提到标记 → 用户文件
        Assert.False(CliShimPath.IsGeneratedShim("#!/bin/sh\necho user\necho DeepSeek Harness Desktop - note\n"));
        Assert.False(CliShimPath.IsGeneratedShim(null));
    }
}

/// <summary>安装计划契约：按平台与 dev 模式产出 dsh/pnpm 文件集、PATH 分隔符与 shell rc 块，以及写入决策矩阵。</summary>
[Collection("cli-shim-env")]
public class CliShimPlannerTests
{
    private const string Runtime = "/home/test/runtime";
    private const string Home = "/home/test/.dsh";
    private const string Bin = "/home/test/.local/bin";

    /// <summary>验证 Windows 完整安装计划产出 dsh/pnpm 的 cmd+ps1 四文件、分号 PATH 分隔且无 shell rc 块。</summary>
    [Fact]
    public void Windows_Setup_Emits_Cmd_Ps1_And_Semicolon_Sep()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Runtime, Home, Bin, isWindows: true, writeDshShim: true);
        Assert.Equal(4, setup.Files.Count);
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("dsh.cmd"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("dsh.ps1"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.cmd"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.ps1"));
        Assert.Equal(";", setup.PathSeparator);
        Assert.Null(setup.ShellRcBlock);
    }

    /// <summary>验证 Windows dev 模式计划跳过 dsh 两种 shim、仍产出 pnpm 两种 shim。</summary>
    [Fact]
    public void Windows_Dev_Setup_Skips_Dsh_Shim()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Runtime, Home, Bin, isWindows: true, writeDshShim: false);
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.EndsWith("dsh.cmd"));
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.EndsWith("dsh.ps1"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.cmd"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.ps1"));
    }

    /// <summary>验证 Unix 完整安装计划产出可执行 dsh/pnpm、冒号 PATH 分隔并带包含 bin 目录的 shell rc 块。</summary>
    [Fact]
    public void Unix_Setup_Emits_Dsh_And_Sh_Block()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Runtime, Home, Bin, isWindows: false, writeDshShim: true);
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("dsh") && f.Executable);
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm") && f.Executable);
        Assert.Equal(":", setup.PathSeparator);
        Assert.NotNull(setup.ShellRcBlock);
        Assert.Contains(Bin, setup.ShellRcBlock);
    }

    /// <summary>验证 Unix dev 模式计划跳过 dsh shim、仍产出可执行 pnpm。</summary>
    [Fact]
    public void Unix_Dev_Setup_Skips_Dsh_Shim()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Runtime, Home, Bin, isWindows: false, writeDshShim: false);
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.EndsWith("dsh"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm"));
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

/// <summary>端到端注册契约：TryRegister 落盘 shim 与 rc 块、重复注册幂等、dev 隔离跳过 dsh、保留用户既有文件；夹具接管并还原进程级 DSH_DESKTOP_CLI_* 环境变量。</summary>
[Collection("cli-shim-env")]
public class CliShimRegistrarTests : IDisposable
{
    private readonly string _binDir;
    private readonly string _rcHome;
    private readonly string _runtime;
    private readonly string _home;
    private readonly string? _oldBinDir;
    private readonly string? _oldRcHome;

    /// <summary>初始化隔离的 CLI shim 测试环境：临时 bin/rc/runtime/home 目录 + 注入 DSH_DESKTOP_CLI_BIN_DIR / DSH_DESKTOP_CLI_RC_HOME，结束时清理。</summary>
    public CliShimRegistrarTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "dsh-cli-shim-" + Guid.NewGuid().ToString("N"));
        _binDir = Path.Combine(root, "bin");
        _rcHome = Path.Combine(root, "rc");
        _runtime = Path.Combine(root, "runtime");
        _home = Path.Combine(root, ".dsh");
        Directory.CreateDirectory(_rcHome);
        File.WriteAllText(Path.Combine(_rcHome, ".bashrc"), "# existing bashrc\n");

        _oldBinDir = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR");
        _oldRcHome = Environment.GetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME");
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR", _binDir);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME", _rcHome);
    }

    /// <summary>还原 DSH_DESKTOP_CLI_* 环境变量并递归删除本夹具的临时目录。</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR", _oldBinDir);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME", _oldRcHome);
        try { Directory.Delete(Path.GetDirectoryName(_binDir)!, recursive: true); } catch { }
    }

    private bool IsWindows() => OperatingSystem.IsWindows();

    /// <summary>验证 TryRegister 落盘 dsh/pnpm shim 且 dsh shim 含生成标记，注册返回成功。</summary>
    [Fact]
    public void TryRegister_Writes_Shims_And_Rc()
    {
        var reg = new CliShimRegistrar(_ => { });
        bool ok = reg.TryRegister(_runtime, _home, isDevIsolated: false);

        Assert.True(ok);
        string dshShim = Path.Combine(_binDir, IsWindows() ? "dsh.cmd" : "dsh");
        string pnpmShim = Path.Combine(_binDir, IsWindows() ? "pnpm.cmd" : "pnpm");
        Assert.True(File.Exists(dshShim), "dsh shim written");
        Assert.True(File.Exists(pnpmShim), "pnpm shim written");
        Assert.Contains(CliShimBuilder.GeneratedMarker, File.ReadAllText(dshShim));
    }

    /// <summary>验证重复注册两次不会在 shell rc 中重复插入 rc 块标记。</summary>
    [Fact]
    public void TryRegister_Is_Idempotent_For_Rc()
    {
        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister(_runtime, _home, isDevIsolated: false);
        reg.TryRegister(_runtime, _home, isDevIsolated: false);

        string rc = File.ReadAllText(Path.Combine(_rcHome, ".bashrc"));
        Assert.Equal(1, Count(rc, CliShimPath.RcBeginMarker));
    }

    /// <summary>验证 dev 隔离注册跳过 dsh shim、仍写入 pnpm shim。</summary>
    [Fact]
    public void TryRegister_Dev_Skips_Dsh_Shim()
    {
        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister(_runtime, _home, isDevIsolated: true);

        string dshShim = Path.Combine(_binDir, IsWindows() ? "dsh.cmd" : "dsh");
        Assert.False(File.Exists(dshShim), "dev must skip dsh shim");
        string pnpmShim = Path.Combine(_binDir, IsWindows() ? "pnpm.cmd" : "pnpm");
        Assert.True(File.Exists(pnpmShim), "pnpm shim still written in dev");
    }

    /// <summary>验证目标 shim 已被用户文件占据时注册不改写其内容。</summary>
    [Fact]
    public void TryRegister_Preserves_User_Shim_File()
    {
        string dshShim = Path.Combine(_binDir, IsWindows() ? "dsh.cmd" : "dsh");
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(dshShim, "#!/bin/sh\necho my real dsh\n");

        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister(_runtime, _home, isDevIsolated: false);

        Assert.Equal("#!/bin/sh\necho my real dsh\n", File.ReadAllText(dshShim));
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
