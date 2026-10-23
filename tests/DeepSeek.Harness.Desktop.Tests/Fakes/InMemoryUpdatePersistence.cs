namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>自更新状态机的内存持久化假件（不落盘）：ready 记录只在内存里，资产存在性恒 false。
/// 供路由/反馈/装配三组测试共用（此前是三份逐字副本）。</summary>
internal sealed class InMemoryUpdatePersistence : UpdateStateMachine.IPersistence
{
    /// <summary>当前 ready 记录；null = 无记录。</summary>
    public UpdateStateMachine.ReadyRecord? Record { get; set; }

    /// <inheritdoc />
    public Task<UpdateStateMachine.ReadyRecord?> GetAsync(CancellationToken ct) => Task.FromResult(Record);

    /// <inheritdoc />
    public Task SetAsync(UpdateStateMachine.ReadyRecord record, CancellationToken ct)
    {
        Record = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct)
    {
        Record = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> AssetExistsAsync(string assetPath, CancellationToken ct) => Task.FromResult(false);
}
