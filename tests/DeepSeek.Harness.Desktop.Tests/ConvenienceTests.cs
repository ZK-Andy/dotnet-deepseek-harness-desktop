using DeepSeek.Harness.Desktop.Services;
using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>就绪横幅脚本：版本嵌入与幂等守卫。</summary>
public class UpdateBannerTests
{
    [Fact]
    public void ReadyScript_LocalizesEnglish()
    {
        // 宿主横幅双语（ADR host-ui-locale）：en 出英文文案，缺省中文
        var en = new Services.UiLocale();
        en.Set("en");
        string script = UpdateBanner.ReadyScript("9.9.9", en);
        Assert.Contains("is ready", script);
        Assert.Contains("textContent=\"OK\"", script);
        Assert.DoesNotContain("已就绪", script);
    }

    [Fact]
    public void ReadyScript_EmbedsVersion_GuardsDoubleInjection()
    {
        string script = UpdateBanner.ReadyScript("9.9.9");
        Assert.Contains("9.9.9", script);
        Assert.Contains("var id='dsh-desktop-update-ready-banner'", script);
        Assert.Contains("if(document.getElementById(id))return;", script);
    }
}

/// <summary>开机自启条目文本构造器（纯函数）。</summary>
public class AutostartBuilderTests
{
    [Fact]
    public void BuildLinuxDesktopEntry_ContainsExecAndAutostartFlag()
    {
        string entry = Autostart.BuildLinuxDesktopEntry("/opt/app/bin/desktop");
        Assert.Contains("Exec=/opt/app/bin/desktop", entry);
        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
    }

    [Fact]
    public void BuildMacOSPlist_PinsLabelArgumentsAndRunAtLoad()
    {
        string plist = Autostart.BuildMacOSPlist("/Applications/App.app/Contents/MacOS/exec");
        Assert.Contains("<key>Label</key>", plist);
        Assert.Contains("io.github.zk-andy.dotnet-deepseek-harness-desktop", plist);
        Assert.Contains("/Applications/App.app/Contents/MacOS/exec", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("<true/>", plist);
    }
}

/// <summary>
/// 安装失败回退契约（批次三固化）：install 委托失败 → 状态机回到 ready、
/// 持久化记录与资产文件原样保留、错误消息经状态帧可见——防未来重构破坏 EAC 语义。
/// </summary>
public class UpdateInstallFailureRecoveryTests
{
    private sealed class FakePersistence : UpdateStateMachine.IPersistence
    {
        public UpdateStateMachine.ReadyRecord? Record { get; private set; }

        public Task<UpdateStateMachine.ReadyRecord?> GetAsync(CancellationToken ct) =>
            Task.FromResult(Record);

        public Task SetAsync(UpdateStateMachine.ReadyRecord record, CancellationToken ct)
        {
            Record = record;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct)
        {
            Record = null;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task InstallAsync_FailingInstall_RevertsToReady_AndKeepsAsset()
    {
        string home = Path.Combine(Path.GetTempPath(), "dsh-eac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            string assetPath = Path.Combine(home, "pkg.deb");
            await File.WriteAllTextAsync(assetPath, "asset-bytes");

            var persistence = new FakePersistence();
            var machine = new UpdateStateMachine(
                currentVersion: "1.0.0",
                check: _ => Task.FromResult<ReleaseMeta?>(new ReleaseMeta(
                    "2.0.0", "x.deb", "https://example/x.deb", Sha256Url: null)),
                download: (_, _) => Task.FromResult(assetPath),
                install: (_, _, _) => throw new InvalidOperationException("授权被取消"),
                persistence,
                onTransition: null);

            await machine.CheckAsync(CancellationToken.None);
            Assert.Equal(UpdateStatus.Ready, machine.State.Status);
            Assert.Equal("2.0.0", machine.State.Version);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => machine.InstallAsync(CancellationToken.None));
            Assert.Equal("授权被取消", ex.Message);

            // 回退契约：ready 态恢复、持久化记录与资产文件原样保留（可重试）
            Assert.Equal(UpdateStatus.Ready, machine.State.Status);
            Assert.Equal("2.0.0", machine.State.Version);
            Assert.NotNull(persistence.Record);
            Assert.Equal(assetPath, persistence.Record!.AssetPath);
            Assert.True(File.Exists(assetPath));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
