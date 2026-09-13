namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 自更新安装包清扫（ADR self-update-prune-consumed-packages）：启动对账时删除「版本已过期」的
/// 安装包与历史废弃残留（<c>install.sh</c>、无持有者的 <c>.download.lock</c>），治 updates 目录
/// 长期只增不减的膨胀（实机曾累积 16 个 rpm / ~1.5GB）。装包成功路径另有 root 脚本用后即清
/// （<see cref="UpdateInstaller.BuildLinuxScript"/>），本器是跨平台兜底 + 存量回收：
/// Windows/macOS 无 root 脚本接管（Inno 直拉新版、dmg 手动装），删包只能靠下次启动对账。
/// 保守性设计：只删「本应用自产、版本严格旧于当前」的整包；解析失败/待装包/半成品一律跳过——
/// 清扫是增强，绝不误删可安装资产。
/// </summary>
public static class StalePackagePruner
{
    /// <summary>本应用安装包文件名前缀（发布命名契约，对齐 <see cref="ReleaseMeta"/> 资产名）。</summary>
    public const string AssetPrefix = "deepseek-harness-desktop-";

    /// <summary>历史废弃形态：root 安装脚本现经 argv 内联（sh -c）传递，不再落盘（见 UpdateInstaller）。</summary>
    public const string LegacyInstallSh = "install.sh";

    /// <summary>下载互斥锁文件名（<see cref="InstallerDownloader.TryAcquireDownloadLock"/>）。</summary>
    public const string DownloadLockFile = ".download.lock";

    /// <summary>下载半成品后缀：下载中文件先写 <c>.part</c> 完成才原子改名（<see cref="InstallerDownloader"/>）。</summary>
    private const string PartSuffix = ".part";

    /// <summary>
    /// 从本应用资产文件名提取版本段：<c>deepseek-harness-desktop-0.4.4_linux-x86_64.rpm</c> →
    /// <c>0.4.4</c>（前缀后至第一个 <c>_</c>）。非本应用文件名或 <c>.part</c> 半成品返回 null
    /// （不参与清扫——半成品归下载器异常路径自清，见 <see cref="InstallerDownloader"/>）。
    /// </summary>
    public static string? TryExtractVersion(string fileName)
    {
        if (!fileName.StartsWith(AssetPrefix, StringComparison.Ordinal) ||
            fileName.EndsWith(PartSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = fileName[AssetPrefix.Length..];
        int underscore = rest.IndexOf('_');
        if (underscore <= 0)
        {
            return null;
        }

        return rest[..underscore];
    }

    /// <summary>
    /// 选择待删文件（纯函数，可单测）：本应用资产 + 版本可解析 + **严格旧于当前** → 删除候选；
    /// 解析失败、版本 ≥ 当前、非资产文件名一律保留。返回待删文件名集合。
    /// </summary>
    /// <param name="fileNames">updates 目录文件名集合。</param>
    /// <param name="currentVersion">当前应用版本（比较基准）。</param>
    /// <remarks>版本比较复用 <see cref="UpdateVersion.Compare"/>；无法解析的版本（脏文件/异常命名）
    /// 保守跳过，清扫不因解析失败引入误删。ready 待装包（恒版本 &gt; 当前，状态机对账 ≤ 当前即清记录）
    /// 天然不在结果内，无需按路径保护。</remarks>
    public static HashSet<string> SelectStale(IEnumerable<string> fileNames, string currentVersion)
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in fileNames)
        {
            string? version = TryExtractVersion(name);
            if (version is null)
            {
                continue;
            }

            try
            {
                if (UpdateVersion.Compare(version, currentVersion) < 0)
                {
                    stale.Add(name);
                }
            }
            catch (ArgumentException)
            {
                // 版本段无法解析：保守跳过，清扫不因脏数据误删
            }
        }

        return stale;
    }

    /// <summary>
    /// 执行启动对账清扫：删除 <see cref="SelectStale"/> 选出的过期包 + <c>install.sh</c> 废弃残留 +
    /// 无持有者的 <c>.download.lock</c> 死锁文件。**下载锁被持有（他实例下载中）时整体跳过**
    /// （ADR 承诺：对账不动在途下载的 .part 与锁，防竞态）。**本方法整体收拢异常**：清扫是增强，
    /// 启动路径不因目录/锁探测的 IO 异常被打死——全项 try/catch 记日志（fail loud），同名其他
    /// best-effort 启动副作用一致（HostLog 兜底）。逐文件删除仍逐个 try/catch，单个失败不阻断其余。
    /// </summary>
    /// <param name="updatesDir">updates 目录。</param>
    /// <param name="currentVersion">当前应用版本。</param>
    /// <param name="log">可选日志注入（宿主接 HostLog）。</param>
    public static void Run(string updatesDir, string currentVersion, Action<string>? log = null)
    {
        try
        {
            RunInner(updatesDir, currentVersion, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[update] 清扫失败（跳过，下次启动重试）：{ex.Message}");
        }
    }

    /// <summary>清扫主体（<see cref="Run"/> 的异常收拢面内）。非 IO/授权/路径类意外异常就此上抛，
    /// 由组合根启动路径兜住（该类异常非清扫能自愈，应可见）。</summary>
    private static void RunInner(string updatesDir, string currentVersion, Action<string>? log)
    {
        if (!Directory.Exists(updatesDir))
        {
            return;
        }

        // 持锁整体跳过前置：下载锁被持有 = 他实例正在下载（.part 在途），本器绝不在途删包/删锁
        string lockPath = Path.Combine(updatesDir, DownloadLockFile);
        if (File.Exists(lockPath) && !CanAcquireLock(lockPath))
        {
            log?.Invoke("[update] 清理跳过：下载锁被持有（他实例下载中）");
            return;
        }

        foreach (string name in SelectStale(Directory.EnumerateFiles(updatesDir).Select(Path.GetFileName).OfType<string>(), currentVersion))
        {
            TryDelete(Path.Combine(updatesDir, name), log);
        }

        // 历史废弃形态：root 脚本不再落盘 install.sh，存量残留删除
        TryDelete(Path.Combine(updatesDir, LegacyInstallSh), log);

        // 无持有者的死锁文件（锁随进程死亡自动释放，0 字节文件残留）：删之无害，下次下载重建
        TryDelete(lockPath, log);
    }

    /// <summary>试探下载锁是否无持有者：FileShare.None 独占打开成功 = 无持有者；IOException = 被占用。</summary>
    private static bool CanAcquireLock(string lockPath)
    {
        try
        {
            using FileStream fs = File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path, Action<string>? log)
    {
        try
        {
            File.Delete(path);
            log?.Invoke($"[update] 清理陈旧安装包：{Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[update] 清理失败（跳过，下次启动重试）：{Path.GetFileName(path)}：{ex.Message}");
        }
    }
}
