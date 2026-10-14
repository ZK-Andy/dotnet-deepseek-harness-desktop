using Microsoft.Extensions.DependencyInjection;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// <see cref="StartupSequence"/> 的监督与退出接线面：监督用例装配（引导落定门控 + 监督循环下沉 Core，
/// ADR 组合根机制收官 step-1 的 B2）、页面健康观测、无边框顶栏注入与启动期告知的按序触发
/// （两用例本体见 <c>PageBridge.CaptionBarInstaller</c> 与 <c>Infrastructure.Bootstrap.StartupNoticeService</c>）。
/// 失败面承载与恢复回调随监督用例住 Core（<c>Core.Bootstrap.RuntimeSupervision</c>）；
/// 启动主链在 <c>StartupSequence.cs</c>。
/// </summary>
internal sealed partial class StartupSequence
{
    private Task SetupSupervisorAsync(HostSetup host)
    {
        // 取消令牌源经构造注入（容器单例，寿命 = 本次 Run；释放随组合根作用域）。
        // 引导落定门控、监督循环、失败面承载与恢复回调全在 Core 用例（ADR 组合根机制收官 step-1 的 B2）；
        // 宿主经阶段产出收参，其余依赖容器供给。返回任务以局部流转，供退出路径 join
        // （ADR sequencer-stage-value-retirement）。
        Core.Bootstrap.RuntimeSupervision supervision =
            _app.App.Services.GetRequiredService<Core.Bootstrap.RuntimeSupervision>();
        return supervision.RunAsync(host.Host, _supervisorCts.Token);
    }

    private void SetupHealthMonitor()
    {
        // 页面健康观测 + 有界恢复（ADR page-health-monitor / reference-alignment 批次五）：
        // 宿主只读探针轮询，不注入不依赖 companion——「dsh 在跑但页面空白」类事故（历史三起全靠
        // 人肉发现）从此有自动留痕；连续 Dead 达阈值后在预算内触发一次有界 reload，耗尽转观测-only，
        // 成功恢复复位预算（防误报引发无限重载循环，对齐参照 plugin_boot.rs 的有界刷新门控）。
        // 首拍延迟（HealthInitialDelaySeconds）避开启动空窗，探针异常按 Unknown 续跑。reload 委托
        // 恒为当前代理靶点（代理源恒定，未铸币时 holder 自 reload）；代理绑定失败时该窗口页面是
        // wwwroot 引导页（有内容 → Alive），不会进入 Dead 恢复分支——空态只是防御性兜底。
        PageHealthMonitor monitor = _app.App.Services.GetRequiredService<PageHealthMonitor>();
        _ = monitor.RunAsync(TimeSpan.FromSeconds(_timeouts.HealthInitialDelaySeconds), _supervisorCts.Token);
    }

    /// <summary>无边框窗口 chrome 注入触发（用例与脚本构建下沉 <see cref="CaptionBarInstaller"/>，ADR 组合根机制
    /// 收官终局的 B3；注入时机与三路语义见该类）：解析用例即装。</summary>
    private void InstallCaptionBar()
    {
        _app.App.Services.GetRequiredService<CaptionBarInstaller>().Install();
    }

    /// <summary>启动期告知任务触发（ADR shared-home-desktop-profile）：用例编排住 <see cref="StartupNoticeService"/>，
    /// 展示面闭包与脏退标志在注册处（<c>StartupUseCaseRegistration</c>）装配；此处只按序触发。</summary>
    private void StartupNoticeTask()
    {
        StartupNoticeService notices = _app.App.Services.GetRequiredService<StartupNoticeService>();
        _ = Task.Run(() => notices.RunAsync(_supervisorCts.Token));
    }
}
