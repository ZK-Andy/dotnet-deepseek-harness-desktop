using System.Runtime.InteropServices;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// macOS 原生窗口增强的单一判据（ADR frameless-uniform-caption-bar）：本进程是否启用<b>原生</b> macOS
/// chrome——毛玻璃 <c>RynOptions.Backdrop</c> 与绿灯的原生全屏语义。窗口选项（组合根）与注入脚本
/// （启动序列）两侧同源此判据，避免「窗口透明/页面不透」的失配。
/// </summary>
/// <remarks>
/// <b>闸界</b>：macOS 且非 x64-under-Rosetta（.NET 7+ 起 Rosetta 进程的
/// <see cref="RuntimeInformation.OSArchitecture"/> 报宿主 <c>Arm64</c>，故以「X64 进程 + Arm64 系统」组合识别转译）。
/// 真实 Intel Mac（X64/X64）与 Apple Silicon 原生（Arm64/Arm64）留在闸内，其余（含非 macOS）在闸外。
/// 排除 Rosetta 的根据、机理推断与降级取舍的单一事实源在 ADR 的「原生 chrome 的平台闸」条——本类只留闸界契约与判据。
/// </remarks>
internal static class MacNativeChrome
{
    /// <summary>判据本体（纯函数，供真值表回归钉）：macOS 且非 Rosetta-x64。</summary>
    /// <param name="isMacOs">本进程是否运行在 macOS 上。</param>
    /// <param name="process">进程架构（随 RID 固定）。</param>
    /// <param name="os">系统架构（Rosetta 转译下报宿主架构）。</param>
    /// <returns>是否启用原生 macOS 窗口增强。</returns>
    internal static bool ShouldEnable(bool isMacOs, Architecture process, Architecture os) =>
        isMacOs && !(process == Architecture.X64 && os == Architecture.Arm64);

    /// <summary>是否启用原生 macOS 窗口增强（判据单点；调用点只读此属性，闸界与理由见类注）。</summary>
    internal static bool IsEnabled =>
        ShouldEnable(
            isMacOs: OperatingSystem.IsMacOS(),
            process: RuntimeInformation.ProcessArchitecture,
            os: RuntimeInformation.OSArchitecture);
}
