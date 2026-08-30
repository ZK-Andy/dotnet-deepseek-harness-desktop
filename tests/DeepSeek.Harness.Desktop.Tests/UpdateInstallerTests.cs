using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>Linux 安装路径中最危险的一段（root 脚本）做内容级回归：包命令、降权拉起链、变量透传。</summary>
public class UpdateInstallerTests
{
    private static readonly Dictionary<string, string> s_fullEnv = new()
    {
        ["DISPLAY"] = ":0",
        ["XAUTHORITY"] = "/run/user/1000/gdm/Xauthority",
        ["WAYLAND_DISPLAY"] = "wayland-0",
        ["XDG_RUNTIME_DIR"] = "/run/user/1000",
        ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus",
        ["PATH"] = "/usr/local/bin:/usr/bin",
        ["HOME"] = "/home/zk",
        ["USER"] = "zk",
        ["DOTNET_ROOT"] = "/usr/lib/dotnet",
    };

    private static string Build(Dictionary<string, string> env) => UpdateInstaller.BuildLinuxScript(
        installCommand: "dpkg -i '/tmp/app.deb'",
        logPath: "/home/zk/.local/share/updates/install.log",
        processId: 4242,
        exePath: "/opt/DeepSeek Harness Desktop/DeepSeek.Harness.Desktop",
        relayEnv: env,
        assetPath: "/home/zk/.local/share/updates/app_0.3.12_linux-amd64.deb",
        assetSha256: "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");

    [Fact]
    public void BuildLinuxScript_VerifiesPackageHashAsRoot_BeforeInstall()
    {
        // TOCTOU 防线：装包前 root 侧按授权时刻冻结的哈希复验包内容，防下载校验后被换包
        string script = Build(s_fullEnv);
        int verifyAt = script.IndexOf("sha256sum -c --status", StringComparison.Ordinal);
        int installAt = script.IndexOf("dpkg -i", StringComparison.Ordinal);
        Assert.True(verifyAt >= 0, "缺 root 侧哈希复验");
        Assert.True(installAt > verifyAt, "复验必须先于装包");
        Assert.Contains("abcdef0123456789", script);
        Assert.Contains("package hash mismatch", script);
    }

    [Fact]
    public void BuildLinuxScript_RefusesSymlinkLog()
    {
        // root 只向用户可写目录追加日志；symlink 重定向必须在装包前拒绝
        string script = Build(s_fullEnv);
        int guardAt = script.IndexOf("is a symlink", StringComparison.Ordinal);
        Assert.True(guardAt >= 0, "缺 symlink 守卫");
        Assert.True(script.IndexOf("dpkg -i", StringComparison.Ordinal) > guardAt, "symlink 守卫必须先于装包");
    }

    [Fact]
    public void BuildLinuxScript_InlineWithoutInstallShFile()
    {
        // 脚本经 sh -c 内联（root 不读用户可写文件），不再出现 install.sh 落盘路径
        string script = Build(s_fullEnv);
        Assert.DoesNotContain("install.sh", script);
    }

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

    [Fact]
    public void BuildLinuxScript_ContainsFullRelayChain()
    {
        string script = Build(s_fullEnv);

        // 等待环：基于进程号
        Assert.Contains("while kill -0 4242 2>/dev/null; do sleep 0.3; done", script);
        // 安装命令原样落进脚本
        Assert.Contains("app exited; running: dpkg -i '/tmp/app.deb'", script);
        Assert.Contains("dpkg -i '/tmp/app.deb'", script);
        // 降权回原用户 + root 下的 runuser 拉起链
        Assert.Contains("REL_USER=\"$(getent passwd \"$PKEXEC_UID\" 2>/dev/null | cut -d: -f1)\"", script);
        // 环境对以「非空字面量单引号」形式透传（生成期决定，空值不出现；按透传清单序排列）
        Assert.Contains("env DISPLAY=':0' XAUTHORITY='/run/user/1000/gdm/Xauthority'", script);
        Assert.Contains("HOME='/home/zk' USER='zk'", script);
        Assert.Contains("DOTNET_ROOT='/usr/lib/dotnet' ", script);
        // 二代实例切主目录 + systemd-run 会话归位分支 + 兜底
        Assert.Contains("cd '/home/zk'", script);
        Assert.Contains("if [ -n \"$DBUS_SESSION_BUS_ADDRESS\" ] && command -v systemd-run >/dev/null 2>&1; then", script);
        Assert.Contains("$RUN_PREFIX nohup '/opt/DeepSeek Harness Desktop/DeepSeek.Harness.Desktop' >> '/home/zk/.local/share/updates/install.log' 2>&1 &", script);
    }

    [Fact]
    public void BuildLinuxScript_EmptyEnvValues_OmittedNotSetEmpty()
    {
        var env = new Dictionary<string, string> { ["DISPLAY"] = ":1" };
        string script = Build(env);

        // 提供的变量以字面量出现，未提供的不得以空串写入二代实例环境
        Assert.Contains("env DISPLAY=':1' $RUN_PREFIX nohup", script);
        Assert.DoesNotContain("DSH_DESKTOP_DSH_HOME=", script);
        // 无 HOME 时 cd 落到 / 兜底
        Assert.Contains("cd '/'", script);
    }

    [Fact]
    public void BuildLinuxScript_NoEnvAtAll_PairsRegionCollapsesToRunPrefix()
    {
        string script = Build(new Dictionary<string, string>());

        Assert.Contains("-- env  $RUN_PREFIX nohup", script);
        Assert.Contains("cd '/'", script);
    }

    [Fact]
    public void BuildLinuxScript_EscapesSingleQuotes_InPathsAndEnv()
    {
        var env = new Dictionary<string, string>
        {
            ["HOME"] = "/ho'me",
            ["DSH_DESKTOP_DSH_HOME"] = "dsh ho'me",
        };
        string script = UpdateInstaller.BuildLinuxScript(
            installCommand: "dpkg -i '/tmp/a'\\''b.deb'",
            logPath: "/x/l'og/install.log",
            processId: 1,
            exePath: "/opt/d'sh",
            relayEnv: env,
            assetPath: "/x/a'pp.deb",
            assetSha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        Assert.Contains("nohup '/opt/d'\\''sh' >> '/x/l'\\''og/install.log' 2>&1 &", script);
        Assert.Contains("HOME='/ho'\\''me'", script);
        Assert.Contains("DSH_DESKTOP_DSH_HOME='dsh ho'\\''me'", script);
        Assert.Contains("cd '/ho'\\''me'", script);
    }
}
