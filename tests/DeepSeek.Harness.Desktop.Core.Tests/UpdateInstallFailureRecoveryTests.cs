namespace DeepSeek.Harness.Desktop.Core.Tests;

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
        /// <summary>内存 fake 不落盘：按记录路径的真实文件存在性返回（与生产语义一致）。</summary>
        public Task<bool> AssetExistsAsync(string assetPath, CancellationToken ct) =>
            Task.FromResult(File.Exists(assetPath));

        public Task ClearAsync(CancellationToken ct)
        {
            Record = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>验证 install 委托抛错后状态机回退到 ready、持久化记录与已下载资产原样保留，可安全重试。</summary>
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
