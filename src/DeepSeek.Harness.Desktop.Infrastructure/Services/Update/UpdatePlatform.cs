namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>Linux 包管理器检测：决定下载 deb 还是 rpm（纯判定可单测）。</summary>
public static class UpdatePlatform
{
    /// <summary>包类型常量。</summary>
    public const string Deb = "deb";
    /// <summary>包类型常量。</summary>
    public const string Rpm = "rpm";

    /// <summary>
    /// 按系统内可执行文件检测包类型：有 dpkg 用 deb，否则有 rpm 用 rpm；
    /// 两者皆无（极简环境）回退 deb（fail-safe：装不上时错误信息仍可诊断）。
    /// </summary>
    public static string DetectPackageKind(bool hasDpkg, bool hasRpm) => hasDpkg ? Deb : hasRpm ? Rpm : Deb;

    /// <summary>探测当前 Linux 系统的包类型；非 Linux 返回 null（win/mac 不需要）。</summary>
    public static string? DetectCurrentPackageKind()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return DetectPackageKind(
            hasDpkg: File.Exists("/usr/bin/dpkg") || File.Exists("/bin/dpkg"),
            hasRpm: File.Exists("/usr/bin/rpm") || File.Exists("/bin/rpm"));
    }
}
