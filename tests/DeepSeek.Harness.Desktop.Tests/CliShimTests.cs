using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>CLI shim 相关测试集合必须真串行：会改写进程级 DSH_DESKTOP_CLI_* 环境变量并行会互相污染。</summary>
[CollectionDefinition("cli-shim-env", DisableParallelization = true)]
public class CliShimEnvCollectionDefinition;

[Collection("cli-shim-env")]
public class CliShimBuilderTests
{
    private const string Runtime = "/home/test/.dsh-desktop/runtime";
    private const string DshHome = "/home/test/.dsh";

    // ------------------------------------------------------------------
    // 转义
    // ------------------------------------------------------------------

    [Fact]
    public void EscapeCmd_Doubles_Percent()
    {
        Assert.Equal(@"C:\Users\100%%test", CliShimBuilder.EscapeCmd(@"C:\Users\100%test"));
    }

    [Fact]
    public void EscapePs1_Doubles_SingleQuote()
    {
        Assert.Equal(@"C:\Users\o''brien", CliShimBuilder.EscapePs1(@"C:\Users\o'brien"));
    }

    [Fact]
    public void EscapeSh_Escapes_SingleQuote()
    {
        Assert.Equal(@"/home/o'\''brien", CliShimBuilder.EscapeSh(@"/home/o'brien"));
    }

    // ------------------------------------------------------------------
    // dsh shim 内容
    // ------------------------------------------------------------------

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

    [Fact]
    public void DshCmd_Escapes_Percent_In_Baked_Path()
    {
        string content = CliShimBuilder.BuildDshCmd(@"C:\Users\100%test\runtime", DshHome);
        Assert.Contains(@"100%%test", content);
        Assert.DoesNotContain(@"set ""RUNTIME_DIR=C:\Users\100%test\runtime""", content);
    }

    [Fact]
    public void DshCmd_Prefers_User_Dsh_Before_Runtime_Launch()
    {
        string content = CliShimBuilder.BuildDshCmd(Runtime, DshHome);
        int userAt = content.IndexOf("USER_DSH", StringComparison.Ordinal);
        int launchAt = content.IndexOf(@"""%NODE%""", StringComparison.Ordinal);
        Assert.True(userAt >= 0 && launchAt > 0 && userAt < launchAt, "user dsh precedence must precede bundled launch");
    }

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

[Collection("cli-shim-env")]
public class CliShimPathTests
{
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

    [Fact]
    public void PathContainsToken_Normalizes_Win_Trailing_Backslash()
    {
        Assert.True(CliShimPath.PathContainsToken(@"C:\A;C:\B\", @"C:\B", ";", caseInsensitive: true));
        Assert.False(CliShimPath.PathContainsToken(@"C:\A;C:\B", @"C:\C", ";", caseInsensitive: true));
    }

    [Fact]
    public void MergePathToken_Empty_Path_Returns_Token()
    {
        string merged = CliShimPath.MergePathToken("", "/home/x/bin", ":", caseInsensitive: false);
        Assert.Equal("/home/x/bin", merged);
    }

    [Fact]
    public void Unix_PathContainsToken_Normalizes_Trailing_Slash()
    {
        Assert.True(CliShimPath.PathContainsToken("/a:/b", "/b/", ":", caseInsensitive: false));
        Assert.False(CliShimPath.PathContainsToken("/a:/b", "/c", ":", caseInsensitive: false));
    }

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

    [Fact]
    public void EnsureShellRcBlock_Appends_After_Existing_Content()
    {
        string block = CliShimPath.BuildShellExportBlock("/home/x/.local/bin", ":");
        string result = CliShimPath.EnsureShellRcBlock("# existing\n", block);
        Assert.StartsWith("# existing", result);
        Assert.Contains(CliShimPath.RcBeginMarker, result);
    }

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

[Collection("cli-shim-env")]
public class CliShimPlannerTests
{
    private const string Runtime = "/home/test/runtime";
    private const string Home = "/home/test/.dsh";
    private const string Bin = "/home/test/.local/bin";

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

    [Fact]
    public void Windows_Dev_Setup_Skips_Dsh_Shim()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Runtime, Home, Bin, isWindows: true, writeDshShim: false);
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.EndsWith("dsh.cmd"));
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.EndsWith("dsh.ps1"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.cmd"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm.ps1"));
    }

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

    [Fact]
    public void Unix_Dev_Setup_Skips_Dsh_Shim()
    {
        CliShimSetup setup = CliShimPlanner.BuildSetup(Runtime, Home, Bin, isWindows: false, writeDshShim: false);
        Assert.DoesNotContain(setup.Files, f => f.TargetPath.EndsWith("dsh"));
        Assert.Contains(setup.Files, f => f.TargetPath.EndsWith("pnpm"));
    }

    [Fact]
    public void DecideShimWrite_Preserves_User_File()
    {
        ShimWriteAction action = CliShimPlanner.DecideShimWrite(exists: true, isGeneratedShim: false, isSymlink: false);
        Assert.Equal(ShimWriteAction.PreserveUserFile, action);
    }

    [Fact]
    public void DecideShimWrite_Writes_Generated_Or_Missing()
    {
        Assert.Equal(ShimWriteAction.Write, CliShimPlanner.DecideShimWrite(exists: false, isGeneratedShim: false, isSymlink: false));
        Assert.Equal(ShimWriteAction.Write, CliShimPlanner.DecideShimWrite(exists: true, isGeneratedShim: true, isSymlink: false));
    }

    [Fact]
    public void DecideShimWrite_Removes_Dangling_Symlink()
    {
        ShimWriteAction action = CliShimPlanner.DecideShimWrite(exists: false, isGeneratedShim: false, isSymlink: true);
        Assert.Equal(ShimWriteAction.RemoveDanglingSymlinkThenWrite, action);
    }
}

[Collection("cli-shim-env")]
public class CliShimRegistrarTests : IDisposable
{
    private readonly string _binDir;
    private readonly string _rcHome;
    private readonly string _runtime;
    private readonly string _home;
    private readonly string? _oldBinDir;
    private readonly string? _oldRcHome;

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

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_BIN_DIR", _oldBinDir);
        Environment.SetEnvironmentVariable("DSH_DESKTOP_CLI_RC_HOME", _oldRcHome);
        try { Directory.Delete(Path.GetDirectoryName(_binDir)!, recursive: true); } catch { }
    }

    private bool IsWindows() => OperatingSystem.IsWindows();

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

    [Fact]
    public void TryRegister_Is_Idempotent_For_Rc()
    {
        var reg = new CliShimRegistrar(_ => { });
        reg.TryRegister(_runtime, _home, isDevIsolated: false);
        reg.TryRegister(_runtime, _home, isDevIsolated: false);

        string rc = File.ReadAllText(Path.Combine(_rcHome, ".bashrc"));
        Assert.Equal(1, Count(rc, CliShimPath.RcBeginMarker));
    }

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
