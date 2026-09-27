namespace DeepSeek.Harness.Desktop.Infrastructure.Update;

/// <summary>
/// 自更新安装包清扫（ADR self-update-prune-consumed-packages）：启动对账时删除「版本已过期」的
/// 安装包与历史废弃残留（<c>install.sh</c>、无持有者的 <c>.download.lock</c>），治 updates 目录
/// 长期只增不减的膨胀（实机曾累积 16 个 rpm / ~1.5GB）。装包成功路径另有 root 脚本用后即清
/// （<see cref="UpdateInstaller.BuildLinuxScript"/>），本器是跨平台兜底 + 存量回收：
/// Windows/macOS 无 root 脚本接管（Inno 直拉新版、dmg 手动装），删包只能靠下次启动对账。
/// 同批对账还把 root 脚本的重启实例日志 <see cref="InstallLogFile"/> 按上限滚代
/// （<see cref="RotateInstallLogIfNeeded"/>，只改名不删内容）。
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

    /// <summary>root 脚本重启实例的 stdout 落点（<see cref="UpdateInstaller.BuildLinuxScript"/> 的
    /// <c>&gt;&gt; install.log</c>，该脚本按本常量拼路径）：既记安装链判定，也沉淀重启实例的整段控制台输出。</summary>
    internal const string InstallLogFile = "install.log";

    /// <summary>install.log 滚动的上一代文件名（覆盖式，只保一代）。</summary>
    internal const string InstallLogRotatedFile = "install.log.1";

    /// <summary>install.log 单文件上限：与 HostLog 同一「单文件日志 5 MiB 封顶」策略。</summary>
    internal const long InstallLogMaxBytes = 5 * 1024 * 1024;

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
    /// 无持有者的 <c>.download.lock</c> 死锁文件，并把超限的 <see cref="InstallLogFile"/> 滚为上一代
    /// （<see cref="RotateInstallLogIfNeeded"/>）。**下载锁被持有（他实例下载中）时整体跳过**
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
        if (PathLinkGuard.IsLink(lockPath))
        {
            // 链接锁：探测与删除都会经链接作用到目标，fail safe 整体跳过（ADR profile-lock-path-symlink-rejection）
            log?.Invoke($"[update] 清理跳过：下载锁是符号链接（{lockPath}）——拒符号链接，不动任何文件");
            return;
        }

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

        // 重启实例的控制台汇无上界（root 脚本 >> install.log，HostLog 每行同时打 stdout）：
        // 超限滚为上一代，目录日志总量有界且内容不丢（不截断不删除）
        RotateInstallLogIfNeeded(Path.Combine(updatesDir, InstallLogFile), InstallLogMaxBytes, log);

        // 无持有者的死锁文件（锁随进程死亡自动释放，0 字节文件残留）：删之无害，下次下载重建
        TryDelete(lockPath, log);
    }

    /// <summary>
    /// install.log 超限滚动（覆盖式一代）：文件大于 <paramref name="maxBytes"/> 时改名
    /// <see cref="InstallLogRotatedFile"/>（覆盖上一代），使 updates 目录日志量有界
    /// （≈ 上限 + 一个重启实例的增量）。**只改本目录内的名字、不截断不删除**——install.log 是
    /// pkexec 授权失败/哈希不匹配类中止的唯一观测面（ADR self-update-pkexec-toctou），证据优先级高于省磁盘。
    /// 受本目录属主即可 <c>rename</c> 之利（root 属主的 644 文件在用户属主目录内可改名，无需文件写权限，已实测）。
    /// </summary>
    /// <param name="logPath">install.log 完整路径。</param>
    /// <param name="maxBytes">单文件上限（字节）。</param>
    /// <param name="log">可选日志注入（宿主接 HostLog）。</param>
    /// <returns>发生滚动返回 <c>true</c>；未超限、文件不存在或跳过返回 <c>false</c>。</returns>
    /// <remarks>链接判定**先于存在性判定**：悬空符号链接下 <see cref="File.Exists(string)"/> 为假，先判存在会静默溜过；
    /// 而 root 脚本的 <c>[ -L ]</c> 守卫对悬空链同样为真、会中止后续每次安装——此处留一行痕迹比静默通过有用
    /// （与同目录下载锁的拒链口径一致，ADR profile-lock-path-symlink-rejection）。</remarks>
    internal static bool RotateInstallLogIfNeeded(string logPath, long maxBytes, Action<string>? log = null)
    {
        try
        {
            if (PathLinkGuard.IsLink(logPath))
            {
                log?.Invoke($"[update] install.log 滚动跳过：是符号链接（{logPath}）");
                return false;
            }

            if (!File.Exists(logPath))
            {
                return false;
            }

            long size = new FileInfo(logPath).Length;
            if (size <= maxBytes)
            {
                return false;
            }

            string? dir = Path.GetDirectoryName(logPath);
            if (string.IsNullOrEmpty(dir))
            {
                // 调用方恒传 Path.Combine 结果（目录 + 文件名）；退化到 CWD 相对改名是错的，宁可留痕跳过
                log?.Invoke($"[update] install.log 滚动跳过：路径无目录部分（{logPath}）");
                return false;
            }

            string rotated = Path.Combine(dir, InstallLogRotatedFile);
            File.Move(logPath, rotated, overwrite: true);
            log?.Invoke($"[update] install.log 超限（{size / 1024}KB），已滚为 {InstallLogRotatedFile}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[update] install.log 滚动失败（跳过，下次启动重试）：{ex.Message}");
            return false;
        }
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
