using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>Linux 安装路径中最危险的一段（root 脚本）做内容级回归：包命令、降权拉起链、变量透传。</summary>
public class UpdateInstallerTests
{
    [Theory]
    [InlineData("/tmp/app_0.1.21_linux-amd64.deb", "dpkg -i '/tmp/app_0.1.21_linux-amd64.deb'")]
    [InlineData("/tmp/app_0.1.21_linux-x86_64.rpm", "rpm -U --replacepkgs --quiet '/tmp/app_0.1.21_linux-x86_64.rpm'")]
    public void InstallCommandFor_DebAndRpm(string path, string expected)
    {
        Assert.Equal(expected, UpdateInstaller.InstallCommandFor(path));
    }

    [Theory]
    [InlineData("x.tar.gz")]
    [InlineData("x.dmg")]
    public void InstallCommandFor_UnsupportedExtension_Throws(string name)
    {
        Assert.Throws<PlatformNotSupportedException>(() => UpdateInstaller.InstallCommandFor(name));
    }

    [Theory]
    [InlineData("X.RPM", "rpm -U --replacepkgs --quiet 'X.RPM'")]
    public void InstallCommandFor_ExtensionIsCaseInsensitive(string path, string expected)
    {
        Assert.Equal(expected, UpdateInstaller.InstallCommandFor(path));
    }

    [Fact]
    public void BuildLinuxScript_ContainsFullRelayChain()
    {
        var script = UpdateInstaller.BuildLinuxScript(
            installCommand: "dpkg -i '/tmp/app.deb'",
            logPath: "/home/zk/.local/share/updates/install.log",
            processId: 4242,
            exePath: "/opt/DeepSeek Harness Desktop/DeepSeek.Harness.Desktop",
            dshHomeOverride: "/home/zk/.local/share/DeepSeek.Harness.Desktop/dsh");

        // 等待环：基于进程号
        Assert.Contains("while kill -0 4242 2>/dev/null; do sleep 0.3; done", script);
        // 安装命令原样落进脚本
        Assert.Contains("app exited; running: dpkg -i '/tmp/app.deb'", script);
        Assert.Contains("dpkg -i '/tmp/app.deb'", script);
        // 降权回原用户 + root 下的 runuser 拉起链
        Assert.Contains("runuser -u \"$REL_USER\" -- env ", script);
        Assert.Contains("REL_USER=\"$(getent passwd \"$PKEXEC_UID\" 2>/dev/null | cut -d: -f1)\"", script);
        // GUI 会话与 DSH 隔离变量透传
        Assert.Contains("DISPLAY=\"$DISPLAY\" WAYLAND_DISPLAY=\"$WAYLAND_DISPLAY\"", script);
        Assert.Contains("DSH_DESKTOP_DSH_HOME=\"/home/zk/.local/share/DeepSeek.Harness.Desktop/dsh\"", script);
        // 二进制路径带空格也能拉起，输出追 install.log
        Assert.Contains("nohup '/opt/DeepSeek Harness Desktop/DeepSeek.Harness.Desktop' >> '/home/zk/.local/share/updates/install.log' 2>&1 &", script);
    }

    [Fact]
    public void BuildLinuxScript_EscapesSingleQuotes_InPaths()
    {
        var script = UpdateInstaller.BuildLinuxScript(
            installCommand: "dpkg -i '/tmp/a'\\''b.deb'",
            logPath: "/x/l'og/install.log",
            processId: 1,
            exePath: "/opt/d'sh",
            dshHomeOverride: "ho'me");

        Assert.Contains("nohup '/opt/d'\\''sh' >> '/x/l'\\''og/install.log' 2>&1 &", script);
        Assert.Contains("DSH_DESKTOP_DSH_HOME=\"ho'\\''me\"", script);
    }
}