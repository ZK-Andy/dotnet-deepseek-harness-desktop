namespace DeepSeek.Harness.Desktop.Core.Platform;

/// <summary>
/// 主窗几何持久化端口：随退出/隐藏/窗口态变化落盘，冷启动读回（ADR 2026-10-25-window-identity-geometry-teardown）。
/// G5 前语义由 Ryn 内建 PersistWindowState 承担（ab2a150），旧宿主下线时随迁丢失，本端口在 Avalonia 宿主重建。
/// 实现必须尽力而为：读不到/损坏/写失败都不允许影响窗口生命周期。
/// </summary>
public interface IWindowGeometryStore
{
    /// <summary>读回上次记住的几何；无记录、不可读或损坏一律 null（调用方回退硬编码缺省）。</summary>
    /// <returns>几何快照；无有效记录为 null。</returns>
    WindowGeometry? Load();

    /// <summary>记住本次几何（尽力而为；写失败仅导致下次启动回退缺省，不抛出）。</summary>
    /// <param name="geometry">当前几何快照。</param>
    void Save(WindowGeometry geometry);
}
