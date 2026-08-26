using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>单次页面探针结论。</summary>
public enum PageHealth
{
	/// <summary>探针异常（窗口未就绪/导航中）——不计入任何计数。</summary>
	Unknown,

	/// <summary>页面有内容。</summary>
	Alive,

	/// <summary>页面空白（body 无子节点）。</summary>
	Dead,
}

/// <summary>
/// 页面健康判定核心（纯逻辑可单测）：连续 <see cref="_deadThreshold"/> 次 Dead 才宣告
/// Dead 迁移——导航瞬间的空窗与渲染间隙不允许触发告警；Unknown 不清零也不累计；
/// 任意 Alive 立即回 Alive 并复位计数。本阶段只做「看见」（迁移留痕日志 + 诊断快照），
/// 不接任何自动恢复动作——误报引发的重启循环比白屏更伤害可用性，是否接线由阶段数据决定
/// （ADR page-health-monitor）。
/// </summary>
public sealed class PageHealthTracker(int deadThreshold = 3)
{
	private readonly int _deadThreshold = deadThreshold > 0 ? deadThreshold : throw new ArgumentOutOfRangeException(nameof(deadThreshold));
	private int _consecutiveDead;

	/// <summary>当前宣告状态（初始 Unknown）。</summary>
	public PageHealth Current { get; private set; } = PageHealth.Unknown;

	/// <summary>累计探针次数（诊断快照用）。</summary>
	public int ProbeCount { get; private set; }

	/// <summary>记录一次探针；发生状态迁移时返回人读描述，否则返回 null。
	/// Unknown 不计入 <see cref="ProbeCount"/>——「probes」语义是有效探针数，窗口未就绪期的
	/// 异常轮询不应让诊断快照里的计数虚高。</summary>
	public string? Record(PageHealth sample)
	{
		if (sample == PageHealth.Unknown)
		{
			return null;
		}

		ProbeCount++;
		switch (sample)
		{
			case PageHealth.Alive:
				_consecutiveDead = 0;
				if (Current == PageHealth.Alive)
				{
					return null;
				}

				Current = PageHealth.Alive;
				return "页面健康：alive";
			case PageHealth.Dead:
				_consecutiveDead++;
				if (Current != PageHealth.Dead && _consecutiveDead >= _deadThreshold)
				{
					Current = PageHealth.Dead;
					return $"页面健康：连续 {_consecutiveDead} 次探针为空（dead）";
				}

				return null;
			default:
				return null;
		}
	}
}

/// <summary>
/// 页面健康轮询观测（阶段 1）：宿主侧定时对 WebView 跑一条只读探针表达式，
/// 不注入脚本、不改页面、不依赖 companion 存活——历史上 companion 自身缺 apply
/// 致整页白屏的事故形态决定了探针不能住在插件里。迁移经日志留痕，最新快照供诊断包收录。
/// 已知边界：dsh 崩溃后恢复页本身是壳的文档（有内容），此阶段按 alive 记录——
/// 该时段进程监督已有独立信号，这里的靶心是「dsh 在跑但页面空白」。
/// </summary>
public sealed class PageHealthMonitor
{
	/// <summary>只读探针：body 有子节点即视为有内容。锚点刻意不绑 dsh 内部组件结构。</summary>
	public const string ProbeScript =
		"(function(){var b=document.body;if(!b)return 'dead';return b.childElementCount===0?'dead':'alive';})()";

	private readonly CurrentWindowAccessor _window;
	private readonly Action<string>? _log;
	private readonly PageHealthTracker _tracker;

	/// <summary>最新健康快照（诊断包 state.txt 收录）；null=尚无有效探针。</summary>
	public string? Snapshot { get; private set; }

	/// <summary>创建监视器。</summary>
	public PageHealthMonitor(CurrentWindowAccessor window, Action<string>? log = null, PageHealthTracker? tracker = null)
	{
		_window = window;
		_log = log;
		_tracker = tracker ?? new PageHealthTracker();
	}

	/// <summary>循环探测直至取消；间隔给足渲染余量，异常路径绝不外抛。</summary>
	public async Task RunAsync(TimeSpan interval, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(interval, ct);
				var raw = await _window.Current.EvaluateJavaScriptAsync(ProbeScript);
				Record(Parse(raw));
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception)
			{
				// 窗口销毁/桥接未就绪等：按 Unknown 处理，静默续跑（观测面绝不拖垮主流程）
				Record(PageHealth.Unknown);
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

	private void Record(PageHealth sample)
	{
		var transition = _tracker.Record(sample);
		if (sample != PageHealth.Unknown)
		{
			Snapshot = $"{sample.ToString().ToLowerInvariant()} @ {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz} (probes {_tracker.ProbeCount})";
		}

		if (transition is not null)
		{
			_log?.Invoke($"[health] {transition}（probes {_tracker.ProbeCount}）");
		}
	}
}
