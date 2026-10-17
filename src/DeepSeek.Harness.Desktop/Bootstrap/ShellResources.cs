using ProxySetup = DeepSeek.Harness.Desktop.Infrastructure.Runtime.DshLoopbackProxy.ProxySetup;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 装配期进程级资源的唯一拥有者（ADR 组合根寿命与配置单一来源 D2）：运行时宿主、回环代理源、
/// 监督器取消令牌源、单实例监听器在此登记并按固定次序释放。职责只有三条——持有、按序释放、幂等；
/// 不构造服务、不做判断、不暴露查询。它存在的原因是 Ryn 不释放容器：平台缺口由本类型补位。
/// </summary>
/// <remarks>释放序 host → proxy → cts → listener：宿主先停子进程并释放句柄；代理随后（内部 cancel +
/// 释放 socket）；取消令牌源此时已无消费者；单实例监听器最后释放 UDS 地址。有序退出路径由
/// <c>ExitPipeline</c> 先行释放（marker 清除有"仅有序退出"语义，故 marker 不在此列），本类型在其后
/// 幂等兜底异常路径。</remarks>
internal sealed class ShellResources : IDisposable
{
    private HarnessRuntimeHost? _host;
    private ProxySetup? _proxy;
    private CancellationTokenSource? _cts;
    private PrimaryListener? _listener;
    private bool _disposed;

    /// <summary>登记运行时宿主（容器惰性工厂创建时回调；首次解析在编排异步相）。</summary>
    /// <param name="host">运行时宿主。</param>
    public void Track(HarnessRuntimeHost host) => _host = host;

    /// <summary>登记回环代理源（Build 前启动的结果对象）。</summary>
    /// <param name="proxy">代理源结果对象。</param>
    public void Attach(ProxySetup proxy) => _proxy = proxy;

    /// <summary>登记监督器取消令牌源（寿命 = 本次 Run）。</summary>
    /// <param name="cts">取消令牌源。</param>
    public void Attach(CancellationTokenSource cts) => _cts = cts;

    /// <summary>登记单实例监听器（未启用时为 null）。</summary>
    /// <param name="listener">监听器；null = 仲裁未启用。</param>
    public void Attach(PrimaryListener? listener) => _listener = listener;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host?.Dispose();
        _proxy?.Dispose();
        _cts?.Dispose();
        _listener?.Dispose();
    }
}
