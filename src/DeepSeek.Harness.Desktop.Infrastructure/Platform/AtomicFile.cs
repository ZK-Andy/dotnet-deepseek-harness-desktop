namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 原子写文本单点（同目录临时文件 + 改名覆盖 + 失败清理临时文件）：写入中途崩溃不产生半写状态。
/// 三个调用点（profile 清单写回 / 崩溃取证 marker / 关闭行为偏好）共用本实现——原子写是
/// 「写盘协议」而非业务语义，各调用点自带的前置（建目录、链接守卫）仍留在调用点。
/// </summary>
public static class AtomicFile
{
    /// <summary>原子写文本（覆盖既有文件）。</summary>
    /// <param name="path">目标文件路径（所在目录须已存在——建目录是调用点的语义，不在此处代劳）。</param>
    /// <param name="text">写入内容（UTF-8）。</param>
    /// <remarks>失败时抛给调用方按各自契约处置（有的调用点要转错误帧，有的不许阻断启动）；
    /// 临时文件在失败路径被清理（清理本身失败不影响结果，只留残留临时文件）。</remarks>
    public static void WriteAllText(string path, string text)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { /* 临时文件清理失败不影响结果 */ }
            throw;
        }
    }
}
