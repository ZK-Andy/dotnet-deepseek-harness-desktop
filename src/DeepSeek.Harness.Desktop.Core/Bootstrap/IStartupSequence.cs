namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>
/// 启动编排端口（R3：接口在 Core、实现在 Presentation 域服务、组合根在容器 <c>Build()</c> 之后显式组装并触发，
/// ADR compose-root-form-separation + 组合根机制收官 step-2a）：
/// 组合根形态分离后阶段编排（profile → 宿主 → spawn → 监督 → 主循环）的唯一执行者。编排器必须由组合根在
/// 容器 <c>Build()</c> 之后显式触发——Ryn 无 Generic Host / hosted-service 机制（<c>RunAsync</c> 强制 thread 0），
/// 容器不能充当启动驱动；本端口终结的是「编排器住在组合根」的旧形态，不是「编排器由容器驱动」。
/// 端口保留（step-3 遗产：闸改向后重议存废，见组合根机制收官落地 ADR）。
/// </summary>
public interface IStartupSequence
{
    /// <summary>执行启动编排主链并返回进程退出码；阻塞直到窗口关闭（Ryn Run 语义）。</summary>
    int Run();
}
