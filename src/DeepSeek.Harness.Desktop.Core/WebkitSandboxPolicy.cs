namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// WebKit 渲染沙箱 userns 受限降级裁决（纯函数可单测，ADR webkit-sandbox-userns-fallback）：
/// WebKitGTK 用 bwrap 隔离 renderer，无特权 userns 被禁的主机上（CI runner 实证两腿 core）
/// 沙箱起不来即整进程崩溃。只在 sysctl 阳性信号下关沙箱，其余一律保持（secure default）。
/// </summary>
public static class WebkitSandboxPolicy
{
    /// <summary>WebKitGTK 沙箱禁用开关（WebKit 官方逃生舱；只在阳性受限信号下由接线层设置，从不 unset）。</summary>
    public const string DisableEnvVar = "WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS";

    /// <summary>userns 总开关（Debian/Fedora 系有此文件）：内容 <c>0</c> = 非特权 userns 被禁。</summary>
    public const string UsernsClonePath = "/proc/sys/kernel/unprivileged_userns_clone";

    /// <summary>AppArmor userns 限制（Ubuntu 23.10+）：内容 <c>1</c> = 非特权 userns 受限。</summary>
    public const string ApparmorRestrictUsernsPath = "/proc/sys/kernel/apparmor_restrict_unprivileged_userns";

    /// <summary>沙箱处置。</summary>
    public enum Disposition
    {
        /// <summary>保持沙箱（默认：非 Linux / 无阳性信号 / 输入缺失或不可解析）。</summary>
        KeepSandbox,

        /// <summary>禁用沙箱（阳性受限信号，接线层设环境变量 + loud 日志）。</summary>
        DisableSandbox,
    }

    /// <summary>
    /// 判定沙箱处置（secure default：只认阳性，不认"疑似"）。
    /// </summary>
    /// <param name="isLinux">是否为 Linux（WebKitGTK/bwrap 只存在于此平台）。</param>
    /// <param name="usernsCloneContent">userns 总开关文件内容（null = 缺失/不可读）。</param>
    /// <param name="apparmorRestrictContent">AppArmor 限制文件内容（null = 缺失/不可读）。</param>
    /// <returns>保持 / 禁用。</returns>
    public static Disposition Evaluate(bool isLinux, string? usernsCloneContent, string? apparmorRestrictContent)
    {
        if (!isLinux)
        {
            return Disposition.KeepSandbox;
        }

        if (usernsCloneContent is not null && usernsCloneContent.Trim() == "0")
        {
            return Disposition.DisableSandbox;
        }

        if (apparmorRestrictContent is not null && apparmorRestrictContent.Trim() == "1")
        {
            return Disposition.DisableSandbox;
        }

        return Disposition.KeepSandbox;
    }
}
