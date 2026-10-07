namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 运行时起步用例端口（R3：端口与用例皆在 Core，ADR 组合根机制收官先行批）：
/// 首启 shim 注册 → 宿主 spawn 等 URL → 壳铸币。原 <c>StartupSequence.StartRuntime</c> 的逐句下沉
/// （零行为变更）；下沉后该链可经 fake 宿主/铸币单测，不再依赖真实 dsh 进程。
/// 同步阻塞（<c>GetAwaiter</c>）留调用链顶端，待 Ryn 线程模型审查后随 Run 链 async 化消除。
/// </summary>
public interface IRuntimeStarter
{
    /// <summary>起宿主、铸币并返回 dsh web 端点；首启引导态或时限内未给 URL 时为 null（降级走 wwwroot）。</summary>
    /// <param name="host">运行时宿主（调用方创建并持有生命周期，本用例只启动不定生死）。</param>
    /// <param name="ct">取消令牌（Run 链 async 化前调用方传 <c>CancellationToken.None</c>）。</param>
    /// <returns>branded 端点；未起即 null。</returns>
    Task<DshWebUrl?> StartAsync(IRuntimeHost host, CancellationToken ct);
}
