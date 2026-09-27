namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>
/// dsh 跨平台启动命令构造（<c>RuntimeBootstrap.DshCommandFor</c>）：Windows 经 cmd 中转、Unix 直跑。
/// 平台可注入重载让 Windows 引号形态在 Linux CI 上也可断言；真机执行面由 Windows 打包冒烟覆盖。
/// </summary>
public class DshCommandTests
{
    /// <summary>验证 Unix 形态：无 binDir 即裸名，有则绝对路径，参数原样透传。</summary>
    [Fact]
    public void DshCommandFor_Unix_BareOrAbsolute()
    {
        DshCommand bare = RuntimeBootstrap.DshCommandFor(null, ["--version"], windows: false);
        Assert.Equal("dsh", bare.Exe);
        Assert.Equal(new[] { "--version" }, bare.Args);

        string bin = Path.Combine(Path.GetTempPath(), "dshbin");
        DshCommand abs = RuntimeBootstrap.DshCommandFor(bin, ["web", "--port", "0"], windows: false);
        Assert.Equal(Path.Combine(bin, "dsh"), abs.Exe);
        Assert.Equal(new[] { "web", "--port", "0" }, abs.Args);
    }

    /// <summary>验证 Windows 形态：经 cmd.exe /d /s /c 中转垫片，参数原样附后（引号由子进程参数表承担）。</summary>
    [Fact]
    public void DshCommandFor_Windows_ViaCmdShim()
    {
        DshCommand bare = RuntimeBootstrap.DshCommandFor(null, ["--version"], windows: true);
        Assert.Equal("cmd.exe", bare.Exe);
        Assert.Equal(new[] { "/d", "/s", "/c", "dsh.cmd", "--version" }, bare.Args);

        string bin = Path.Combine("C:", "npm");
        DshCommand abs = RuntimeBootstrap.DshCommandFor(bin, ["web", "--port", "0"], windows: true);
        Assert.Equal("cmd.exe", abs.Exe);
        Assert.Equal(
            new[] { "/d", "/s", "/c", Path.Combine(bin, "dsh.cmd"), "web", "--port", "0" },
            abs.Args);
    }

    /// <summary>验证含空格的 Windows 前缀仍以单个 argv 元素承载垫片路径（不手拼引号串）。</summary>
    [Fact]
    public void DshCommandFor_Windows_SpacedPrefix_SingleElement()
    {
        string bin = Path.Combine("C:", "Program Files", "nodejs");
        DshCommand cmd = RuntimeBootstrap.DshCommandFor(bin, ["--version"], windows: true);
        Assert.Equal("cmd.exe", cmd.Exe);
        Assert.Equal(5, cmd.Args.Count);
        Assert.Equal(Path.Combine(bin, "dsh.cmd"), cmd.Args[3]);
    }

    /// <summary>验证垫片路径映射：Windows dsh.cmd、Unix dsh，null 即 PATH 语义裸名。</summary>
    [Fact]
    public void DshShimPath_PlatformMapping()
    {
        Assert.Equal("dsh.cmd", RuntimeBootstrap.DshShimPath(null, windows: true));
        Assert.Equal("dsh", RuntimeBootstrap.DshShimPath(null, windows: false));
        Assert.Equal(
            Path.Combine("C:", "npm", "dsh.cmd"),
            RuntimeBootstrap.DshShimPath(Path.Combine("C:", "npm"), windows: true));
    }
}
