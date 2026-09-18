namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 接力就绪的稳定窗（ADR relay-restart-client-module-collapse）：单次 <c>Ready</c> 不足以证明续任者已接稳——
/// 市场自重启窗口里将死的前驱进程仍会应答 web 面（实测 401 + 68 字节正文），按单次判据即被收养，WebView
/// 随即落进「连上随即断线」的窗口（页面重连后整树 client 模块热替换、服务失联、会话与归档列表全空且不自愈）。
/// 本类要求 Ready **连续维持** 达稳定窗且期间不出现任何未就绪采样，才判定「已接稳」；任一非 Ready 采样即
/// 清零重计。降概率不兜底：真落进窗口仍由页面健康探针的内容判据 + 有界 reload 复原。纯逻辑（时刻由调用方
/// 传入）可单测。
/// </summary>
/// <param name="stableWindow">就绪须连续维持的时长；与接力探测节拍同源（节拍 1s 下 ≥2s 即 ≥3 拍连续 Ready）。</param>
public sealed class RelayWebReadinessGate(TimeSpan stableWindow)
{
    private readonly TimeSpan _stableWindow = stableWindow > TimeSpan.Zero
        ? stableWindow
        : throw new ArgumentOutOfRangeException(nameof(stableWindow));

    private DateTimeOffset? _readySince;

    /// <summary>喂入一次采样；返回 true 表示 Ready 已连续维持达稳定窗（可进收养链）。</summary>
    /// <param name="sample">本次 web 面就绪三态。</param>
    /// <param name="now">本次采样时刻。</param>
    /// <returns>true=已接稳；false=尚未（首拍，或中途出现过未就绪而重新计时）。</returns>
    public bool Observe(RuntimeLineageProbes.LoopbackWebProbe sample, DateTimeOffset now)
    {
        if (sample != RuntimeLineageProbes.LoopbackWebProbe.Ready)
        {
            _readySince = null;
            return false;
        }

        _readySince ??= now;
        return now - _readySince >= _stableWindow;
    }
}
