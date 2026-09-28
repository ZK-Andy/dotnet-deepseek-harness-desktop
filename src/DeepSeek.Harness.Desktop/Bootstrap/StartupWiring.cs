using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动期惰性接线槽（ADR compose-root-form-separation）：组合根与注册闭包原先持有「早于赋值的惰性捕获」
/// 字段（注册时点早于赋值时点，参数不可能先于自身构造流入），编排搬出根后这些槽位随迁本对象——
/// 注册闭包捕获本实例，槽位由后到期的阶段回填，语义与搬迁前逐一等价。仅启动编排期存活，不承载域状态。
/// </summary>
internal sealed class StartupWiring
{
    /// <summary>Ryn 应用实例（Build 后回填；此前触达 = 编排错误，与搬迁前字段语义一致）。</summary>
    public Ryn.Core.RynApplication? App { get; set; }

    /// <summary>当前窗口访问器（Build 后回填；Build 前为 null，消费方按可空处理）。</summary>
    public CurrentWindowAccessor? WindowAccessor { get; set; }

    /// <summary>运行时宿主（编排阶段创建后回填；引导服务经惰性委托读取，读取时点必已回填）。</summary>
    public HarnessRuntimeHost? Host { get; set; }

    /// <summary>监督器取消令牌源（监督器装配后回填；Run 尾部释放的唯一 CTS 槽位）。</summary>
    public CancellationTokenSource? SupervisorCts { get; set; }

    /// <summary>监督器取消令牌（未装配时 None——自更新后台任务等消费方的惰性读点）。</summary>
    public CancellationToken SupervisorToken => SupervisorCts?.Token ?? CancellationToken.None;

    /// <summary>托盘控制器（组合根装配期回填；启动极早期的激活请求按 null 静默忽略）。</summary>
    public Tray.TrayController? Tray { get; set; }

    /// <summary>单实例退出管道（监督器装配期回填；托盘退出/自更新兜底触发时必已就绪）。</summary>
    public ExitPipeline? Exit { get; set; }

    /// <summary>页面健康监视器（后台装配期回填；诊断快照消费方按可空处理）。</summary>
    public PageHealthMonitor? HealthMonitor { get; set; }

    /// <summary>恢复周期起点（恢复屏展示时刻写入、收养导航读出：周期内有导航到达即页内已自刷）。</summary>
    public DateTimeOffset LastRecoveryShownAtUtc { get; set; }
}
