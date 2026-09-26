namespace DeepSeek.Harness.Desktop;

/// <summary>
/// <see cref="DesktopBootstrap"/> 的 WebKit 沙箱降级面（尺寸健康闸拆分，ADR webkit-sandbox-userns-fallback）：
/// userns 受限主机上 bwrap 沙箱起不来即整进程 core。决策在 <c>Core.WebkitSandboxPolicy</c> 纯策略，
/// 此处仅在 WebView 创建前编排 sysctl 采样/环境变量/日志（R1 组合根只装配）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>WebKit 沙箱 userns 降级（ADR webkit-sandbox-userns-fallback）：`Run()` 入口首行调用，
    /// 先于一切 WebView 创建（renderer 继承本进程 env）。非 Linux 直接返回；sysctl 缺失/不可读按未知
    /// 保持沙箱；阳性受限才设禁用变量 + loud 日志。读文件失败不抛（观测失败不断启动链）。</summary>
    private static void ApplyWebkitSandboxFallback()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string? userns = TryReadSysctl(Core.WebkitSandboxPolicy.UsernsClonePath);
        string? apparmor = TryReadSysctl(Core.WebkitSandboxPolicy.ApparmorRestrictUsernsPath);
        if (Core.WebkitSandboxPolicy.Evaluate(true, userns, apparmor) != Core.WebkitSandboxPolicy.Disposition.DisableSandbox)
        {
            return;
        }

        Environment.SetEnvironmentVariable(Core.WebkitSandboxPolicy.DisableEnvVar, "1");
        HostLog.Write("[host] WebKit 沙箱已禁用（userns 受限），renderer 无隔离");
    }

    /// <summary>读 sysctl 单行文件；缺失/不可读/异常一律 null（调用方按未知保持沙箱，secure default）。</summary>
    private static string? TryReadSysctl(string path)
    {
        try
        {
            return File.ReadAllText(path); // verify-code-conventions: ignore 组合根启动采样：sysctl 单行读，早于服务装配无注入点，失败即null
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
