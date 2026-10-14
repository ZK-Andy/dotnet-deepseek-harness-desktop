namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 启动编排端口（R3：接口在 Core、实现在 Presentation 域服务、组合根在容器 <c>Build()</c> 之后显式组装并触发，
/// ADR compose-root-form-separation + 组合根机制收官 step-2a）：
/// 组合根形态分离后阶段编排（profile → 宿主 → spawn → 监督 → 主循环）的唯一执行者。编排器必须由组合根在
/// 容器 <c>Build()</c> 之后显式触发——Ryn 无 Generic Host / hosted-service 机制（<c>RunAsync</c> 强制 thread 0），
/// 容器不能充当启动驱动；本端口终结的是「编排器住在组合根」的旧形态，不是「编排器由容器驱动」。
/// 两相入口（ADR entry-bridge-two-phase）：异步相只含 I/O 起步链（由接线层桥一次），同步相在 thread 0
/// 承接 Ryn 面尾段与主循环——故端口不是一个 <c>Run()</c>，而是相位对齐的两步。
/// </summary>
public interface IStartupSequence
{
    /// <summary>启动异步相：profile 前置、宿主产出、随包插件安装与运行时起步（本相唯一可 await 的 I/O 链；
    /// 续体在池线程，故 Ryn 面调用不在此相）。</summary>
    /// <returns>本相完成（起步已落定）即完成的任务。</returns>
    Task RunStartupAsync();

    /// <summary>同步相（thread 0）：启动链尾段、主循环与退出编排；阻塞直到窗口关闭（Ryn Run 语义）。</summary>
    /// <returns>进程退出码。</returns>
    int RunMainLoop();
}
