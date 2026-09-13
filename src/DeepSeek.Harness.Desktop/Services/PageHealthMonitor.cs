using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 页面健康轮询观测 + 有界恢复：宿主侧定时对 WebView 跑一条只读探针表达式，
/// 不注入脚本、不改页面、不依赖 companion 存活——历史上 companion 自身缺 apply
/// 致整页白屏的事故形态决定了探针不能住在插件里。迁移经日志留痕，最新快照供诊断包收录。
/// 已知边界：dsh 崩溃后恢复页本身是壳的文档（有内容），此阶段按 alive 记录——
/// 该时段进程监督已有独立信号，这里的靶心是「dsh 在跑但页面空白」。
/// 有界恢复：连续 Dead 达阈值宣告、且注入 <paramref name="reload"/> 恢复委托时，在预算内
/// 触发一次 reload（ADR reference-alignment 批次五）；预算耗尽转入观测-only，成功恢复复位
/// 预算窗口（见 <see cref="PageHealthRecovery"/>）。<paramref name="reload"/> 为 null 时保持纯观测
/// （阶段 1 行为，向后兼容）。
/// </summary>
public sealed class PageHealthMonitor
{
    /// <summary>只读探针：body 有子节点即视为有内容。锚点刻意不绑 dsh 内部组件结构。</summary>
    public const string ProbeScript =
        "(function(){var b=document.body;if(!b)return 'dead';return b.childElementCount===0?'dead':'alive';})()";

    private readonly CurrentWindowAccessor _window;
    private readonly Action<string>? _log;
    private readonly PageHealthTracker _tracker;
    private readonly PageHealthRecovery _recovery;
    private readonly Func<CancellationToken, ValueTask>? _reload;

    /// <summary>最新健康快照（诊断包 state.txt 收录）；null=尚无有效探针。</summary>
    public string? Snapshot { get; private set; }

    /// <summary>创建监视器。</summary>
    /// <param name="window">当前窗口访问器（读探针与导航共用）。</param>
    /// <param name="log">日志输出（可选）。</param>
    /// <param name="tracker">页面健康判定器（测试可注入；默认新实例）。</param>
    /// <param name="recovery">有界恢复预算（测试可注入；默认新实例）。</param>
    /// <param name="reload">有界恢复的 reload 委托；null=纯观测（向后兼容阶段 1）。纯观测与否
    /// 由 <paramref name="reload"/> 是否为 null 决定，预算对象与观测无关。</param>
    public PageHealthMonitor(
        CurrentWindowAccessor window,
        Action<string>? log = null,
        PageHealthTracker? tracker = null,
        PageHealthRecovery? recovery = null,
        Func<CancellationToken, ValueTask>? reload = null)
    {
        _window = window;
        _log = log;
        _tracker = tracker ?? new PageHealthTracker();
        _recovery = recovery ?? new PageHealthRecovery();
        _reload = reload;
    }

    /// <summary>循环探测直至取消；间隔给足渲染余量，异常路径绝不外抛。</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                string raw = await _window.Current.EvaluateJavaScriptAsync(ProbeScript);
                Record(Parse(raw), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 窗口销毁/桥接未就绪等：按 Unknown 处理，静默续跑（观测面绝不拖垮主流程）
                Record(PageHealth.Unknown, ct);
            }
        }
    }

    /// <summary>探针结果解析（纯函数可单测）：容忍桥接的引号包裹与空白。</summary>
    public static PageHealth Parse(string? raw) => raw?.Trim().Trim('"') switch
    {
        "alive" => PageHealth.Alive,
        "dead" => PageHealth.Dead,
        _ => PageHealth.Unknown,
    };

    private void Record(PageHealth sample, CancellationToken ct)
    {
        string? transition = _tracker.Record(sample);
        if (sample != PageHealth.Unknown)
        {
            Snapshot = $"{sample.ToString().ToLowerInvariant()} @ {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz} (probes {_tracker.ProbeCount})";
        }

        if (transition is not null)
        {
            _log?.Invoke($"[health] {transition}（probes {_tracker.ProbeCount}）");
            HandleTransition(_tracker.Current, ct);
        }
    }

    /// <summary>Dead/Alive 迁移处的有界恢复裁决（internal 供接线级回归测试）。</summary>
    internal void HandleTransition(PageHealth current, CancellationToken ct)
    {
        // Alive（含 Unknown→Alive 与 Dead→Alive 恢复）：复位有界恢复预算窗口——本次死区结束，
        // 下次假活（新死区）可重新有界恢复。无论是否配置 reload 都复位预算为无害幂等。
        if (current == PageHealth.Alive)
        {
            _recovery.MarkRecovered();
            return;
        }

        // Dead 迁移：未配置 reload 委托（纯观测）则无恢复动作。
        if (current != PageHealth.Dead || _reload is null)
        {
            return;
        }

        if (!_recovery.TryAllowRecovery())
        {
            _log?.Invoke($"[health] 有界恢复达上限（{_recovery.Attempts}），停止自动恢复，转观测");
            return;
        }

        _tracker.ReArm(); // 重置死区：reload 后仍空白可重新凑满阈值再触发，实现同一死区有界多次恢复
        _log?.Invoke($"[health] 触发有界恢复 reload #{_recovery.Attempts}");
        _ = ReloadAsync(ct);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        try
        {
            await _reload!(ct);
        }
        catch (OperationCanceledException)
        {
            // 取消（应用退出/监督器终止）：静默——恢复动作随主流程撤销
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[health] 有界恢复 reload 失败：{ex.Message}");
        }
    }
}
