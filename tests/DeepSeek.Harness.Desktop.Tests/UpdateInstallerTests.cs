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

    /// <summary>验证 root 侧在装包前以 sha256sum 复验冻结的包哈希（TOCTOU 防线），哈希不一致以「package hash mismatch」退出。</summary>
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

    /// <summary>验证日志路径为 symlink 时脚本在装包前拒绝执行，root 不向用户可控位置追加日志。</summary>
    [Fact]
    public void BuildLinuxScript_RefusesSymlinkLog()
    {
        // root 只向用户可写目录追加日志；symlink 重定向必须在装包前拒绝
        string script = Build(s_fullEnv);
        int guardAt = script.IndexOf("is a symlink", StringComparison.Ordinal);
        Assert.True(guardAt >= 0, "缺 symlink 守卫");
        Assert.True(script.IndexOf("dpkg -i", StringComparison.Ordinal) > guardAt, "symlink 守卫必须先于装包");
    }

    /// <summary>验证脚本经 sh -c 内联执行（root 不读用户可写文件），产物不再出现 install.sh 落盘路径。</summary>
    [Fact]
    public void BuildLinuxScript_InlineWithoutInstallShFile()
    {
        // 脚本经 sh -c 内联（root 不读用户可写文件），不再出现 install.sh 落盘路径
        string script = Build(s_fullEnv);
        Assert.DoesNotContain("install.sh", script);
    }

    /// <summary>验证 .deb 扩展名映射 dpkg -i、.rpm 映射 rpm -U --replacepkgs，安装命令与预期逐字一致。</summary>
    [Theory]
    [InlineData("/tmp/app_0.1.21_linux-amd64.deb", "dpkg -i '/tmp/app_0.1.21_linux-amd64.deb'")]
    [InlineData("/tmp/app_0.1.21_linux-x86_64.rpm", "rpm -U --replacepkgs --quiet '/tmp/app_0.1.21_linux-x86_64.rpm'")]
    public void InstallCommandFor_DebAndRpm(string path, string expected)
    {
        Assert.Equal(expected, UpdateInstaller.InstallCommandFor(path));
    }

    /// <summary>验证 .tar.gz/.dmg 等非 .deb/.rpm 扩展名抛出 PlatformNotSupportedException。</summary>
    [Theory]
    [InlineData("x.tar.gz")]
    [InlineData("x.dmg")]
    public void InstallCommandFor_UnsupportedExtension_Throws(string name)
    {
        Assert.Throws<PlatformNotSupportedException>(() => UpdateInstaller.InstallCommandFor(name));
    }

    /// <summary>验证脚本含按进程号的等待环、安装命令原样落进、runuser 降权回原用户、环境按字面量序透传与 systemd-run 会话归位分支。</summary>
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

    /// <summary>验证未提供的变量不以空串写入二代实例环境，且无 HOME 时 cd 落到 / 兜底。</summary>
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

    /// <summary>验证 env 对为空时环境区退化为空并直接衔接 RUN_PREFIX，脚本产出仍合法。</summary>
    [Fact]
    public void BuildLinuxScript_NoEnvAtAll_PairsRegionCollapsesToRunPrefix()
    {
        string script = Build(new Dictionary<string, string>());

        Assert.Contains("-- env  $RUN_PREFIX nohup", script);
        Assert.Contains("cd '/'", script);
    }

    /// <summary>验证路径、日志路径与环境值中的单引号均按 sh 转义规则处理，字面值无损嵌入脚本、不破坏结构。</summary>
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

    /// <summary>验证装包成功后脚本删除安装包（用后即清）：rm 落在 install 成功判定之后、relaunch 之前。</summary>
    [Fact]
    public void BuildLinuxScript_RemovesPackage_AfterSuccessfulInstall_BeforeRelaunch()
    {
        string script = Build(s_fullEnv);

        // 装包结果先存 install_rc，成功分支才 rm 安装包
        Assert.Contains("install_rc=$?", script);
        Assert.Contains("if [ \"$install_rc\" -eq 0 ]; then", script);
        int rmAt = script.IndexOf("rm -f '/home/zk/.local/share/updates/app_0.3.12_linux-amd64.deb'", StringComparison.Ordinal);
        int relaunchAt = script.IndexOf("nohup", StringComparison.Ordinal);
        Assert.True(rmAt >= 0, "缺装包成功后删除安装包");
        Assert.True(rmAt < relaunchAt, "删包必须先于 relaunch（root 尚未降权）");
    }

    /// <summary>验证装包结果先存 install_rc 再判定：rm 删除命令位于「成功分支守卫（if eq 0）」之内——
    /// 失败不删包（保留供重试/诊断），删包失败仅留日志（|| echo）不阻断 relaunch。</summary>
    [Fact]
    public void BuildLinuxScript_CleanupGuardedByInstallSuccess_AndNonFatal()
    {
        string script = Build(s_fullEnv);

        // 装包结果先存 install_rc（后续 echo/判定不污染 $?）
        Assert.Contains("install_rc=$?", script);
        Assert.Contains("echo \"install exit=$install_rc\"", script);
        // rm 只出现在成功守卫（if [ "$install_rc" -eq 0 ]）的 then 体内、且带失败容忍：
        // 失败分支（else/守卫外）绝不删包——用 then/fi 词法界定真钉
        string guard = "if [ \"$install_rc\" -eq 0 ]; then";
        int guardAt = script.IndexOf(guard, StringComparison.Ordinal);
        Assert.True(guardAt >= 0, "缺装包成功守卫");
        // then 与紧随其后的 fi 之间的区块 = 成功分支
        int thenAt = guardAt + guard.Length;
        int fiAt = script.IndexOf("fi", thenAt, StringComparison.Ordinal);
        string successBlock = script[thenAt..fiAt];
        int rmAt = successBlock.IndexOf("rm -f '/home/zk/.local/share/updates/app_0.3.12_linux-amd64.deb'", StringComparison.Ordinal);
        Assert.True(rmAt >= 0, "rm 必须在成功守卫的 then 体内（失败不删包）");
        Assert.Contains("|| echo \"package cleanup failed", successBlock);
    }
}
