namespace DeepSeek.Harness.Desktop.Infrastructure.Tests;

/// <summary>
/// 仅 Unix（Linux/macOS）执行的用例：单实例仲裁（Unix domain socket）、shell rc 注册等语义在 Windows
/// 按平台边界关闭（<c>LauncherActivation.TryBindPrimary</c> 直接放行、<c>CliShimRegistrar</c> 改写
/// HKCU 环境变量而非 rc 文件），这些形状在 Windows 上没有对应语义，跑起来只是把 Unix 假设当断言。
/// </summary>
/// <remarks>skip 原因是契约文字而非「暂未支持」：产品行为由 ADR single-instance-launcher-activation
/// 与 windows-dsh-spawn-unified 固定，短跳而非改断言，避免把平台边界误记成缺陷。默认文案是中性的，
/// 个别用例若因额外原因（如 Windows 无注册表注入 seam）跳过，用带原因的构造传入。</remarks>
public sealed class UnixFactAttribute : FactAttribute
{
    /// <summary>在 Windows 上把用例标记为跳过；原因缺省为平台契约关闭。</summary>
    public UnixFactAttribute()
        : this(null)
    {
    }

    /// <summary>在 Windows 上把用例标记为跳过，并写清该用例的具体原因。</summary>
    /// <param name="reason">跳过原因（进 CI 报告）；为空时用通用平台契约文案。</param>
    public UnixFactAttribute(string? reason)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = reason ?? "Unix-only 语义：该不变量在 Windows 上按平台契约关闭";
        }
    }
}

/// <summary>
/// <see cref="UnixFactAttribute"/> 的 Theory 形态（带 <c>[InlineData]</c> 的 Unix-only 用例）。
/// </summary>
public sealed class UnixTheoryAttribute : TheoryAttribute
{
    /// <summary>在 Windows 上把用例标记为跳过（含原因，CI 报告可见）。</summary>
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only 语义：该不变量在 Windows 上按平台契约关闭";
        }
    }
}

/// <summary>
/// 仅 Linux 执行的用例：依赖 <c>/proc</c> 父链（血统探针）或 <c>/bin/sh</c> 夹具，Windows/macOS 上
/// 探针恒返回 null，跑起来只会「凡断必过」。不得用早退 <c>return</c> 掩饰未执行。
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    /// <summary>在非 Linux 上把用例标记为跳过（含原因，CI 报告可见）。</summary>
    public LinuxFactAttribute()
        : this(null)
    {
    }

    /// <summary>在非 Linux 上把用例标记为跳过，并写清该用例的具体原因。</summary>
    /// <param name="reason">跳过原因（进 CI 报告）；为空时用血统探针的通用文案。</param>
    public LinuxFactAttribute(string? reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = reason ?? "Linux-only 语义：依赖 /proc 父链与 /bin/sh 夹具";
        }
    }
}
