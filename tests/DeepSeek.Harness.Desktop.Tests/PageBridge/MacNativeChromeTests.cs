using System.Runtime.InteropServices;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>原生 macOS chrome 的平台闸真值表（ADR frameless-uniform-caption-bar）：判据是纯函数，
/// 故用真值表钉死而不是重述 ambient 值（后者是循环证明）。闸界与落闸根据见 <c>MacNativeChrome</c> 类注与 ADR
/// ——这里只断言期望值。</summary>
public class MacNativeChromeTests
{
    /// <summary>真值表：仅 Rosetta-x64 被排除。</summary>
    /// <param name="isMacOs">本进程是否运行在 macOS 上。</param>
    /// <param name="process">进程架构。</param>
    /// <param name="os">系统架构。</param>
    /// <param name="expected">期望的闸值。</param>
    [Theory]
    [InlineData(true, Architecture.X64, Architecture.Arm64, false)]   // Rosetta：x64 产物跑在 AS 上
    [InlineData(true, Architecture.X64, Architecture.X64, true)]     // 真实 Intel Mac
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, true)] // Apple Silicon 原生
    [InlineData(false, Architecture.X64, Architecture.X64, false)]   // 非 macOS 的常见形态（Windows/Linux x64）
    [InlineData(false, Architecture.Arm64, Architecture.Arm64, false)]
    public void ShouldEnable_TruthTable(bool isMacOs, Architecture process, Architecture os, bool expected) =>
        Assert.Equal(expected, MacNativeChrome.ShouldEnable(isMacOs, process, os));
}
