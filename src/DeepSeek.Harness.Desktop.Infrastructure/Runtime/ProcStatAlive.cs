namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>Linux <c>/proc</c> 进程状态判活（ADR residue-alive-zombie-recheck）：读
/// <c>/proc/&lt;pid&gt;/stat</c> 状态位——SIGKILL 后僵尸在被父进程收割前仍占 <c>/proc/&lt;pid&gt;</c>
/// 目录，纯目录存在性判定会把已死残留误报成「杀后仍活」触发 fail-loud 停摆（2026-10-25 实机：
/// pid 795643 击杀成功却被误判存活 17s）。僵尸不占端口不占资源，对收养/清扫/残留判定全部语境
/// 「死」语义一致；状态位解析为纯函数，可单测。</summary>
internal static class ProcStatAlive
{
    /// <summary>Linux 判活：stat 不存在或状态位 <c>Z</c>（僵尸）按死算；stat 读不到但目录在
    /// （权限等异常）保守按活（fail loud 方向，不静默放行撞端口）。</summary>
    public static bool IsAlive(int pid)
    {
        string dir = $"/proc/{pid}";
        if (!Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            return AliveStateChar(ExtractStateChar(File.ReadAllText(Path.Combine(dir, "stat"))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>从 <c>/proc/&lt;pid&gt;/stat</c> 原文提取进程状态位（comm 可含空格与括号，取最后一个
    /// <c>)</c> 之后的第一个字符）；无状态位返回 null。</summary>
    public static char? ExtractStateChar(string statText)
    {
        int commEnd = statText.LastIndexOf(')');
        return statText.Length > commEnd + 2 && statText[commEnd + 1] == ' '
            ? statText[commEnd + 2]
            : null;
    }

    /// <summary>状态位是否算存活：Z（僵尸，已死待收割，不占端口不占资源）与 null（stat 无状态位，
    /// 仅内核格式异常时可达）按死算——stat 读取异常（不可判定）已在 <see cref="IsAlive"/> 单独保守按活，
    /// 此处只消费「已读到的状态位」。</summary>
    public static bool AliveStateChar(char? state) => state is not (null or 'Z');
}
