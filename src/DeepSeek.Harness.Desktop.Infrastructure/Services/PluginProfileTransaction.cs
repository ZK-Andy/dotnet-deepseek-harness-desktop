using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>事务管线 journal 的当前步（<see cref="PluginProfileTransaction"/> 换入推进度）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ProfileTxStep
{
    /// <summary>staging 已就绪、尚未动 active。</summary>
    Prepared,

    /// <summary>active 已让位到 rollback 目录、staging 尚未换入（两步 rename 之间的窗口态）。</summary>
    ActiveMoved,

    /// <summary>staging 已换入 active，journal 删除被中断的尾巴态。</summary>
    StagingActivated,
}

/// <summary>
/// 事务化插件管线（ADR transactional-plugin-pipeline，对照官方 project-manager 的 staging→体检→日志化换入）：
/// 插件变更不再原地改写 active 桌面 profile，而是整目录拷贝出 staging 副本、在副本上施加变更并体检，
/// 通过后经 pending journal 引导的两步 rename 换入——active 任何时刻要么旧完整态要么新完整态，
/// 中断/崩溃由启动期 <see cref="Recover"/> 重放或回滚收口；journal 损坏 fail loud。
/// </summary>
/// <remarks>
/// 目录约定（均在同一卷、rename 原子）：
/// staging home = <c>&lt;dshHome&gt;/.tx-&lt;uuid&gt;/</c>（其 <c>profiles/&lt;DesktopProfileName&gt;</c> 为变更副本）；
/// rollback = <c>&lt;dshHome&gt;/profiles/.rollback-&lt;uuid&gt;/</c>；
/// journal = <c>&lt;dshHome&gt;/profiles/.pending.json</c>。
/// 事务只允许在 spawn 前窗口执行（运行中 dsh 不装，与既有约定一致）。
/// </remarks>
public sealed class PluginProfileTransaction
{
    /// <summary>journal 文件名（位于 <c>profiles/</c> 根）。</summary>
    internal const string PendingFileName = ".pending.json";
    /// <summary>journal schema 版本：字段形状变更时递增，recover 对不认识的版本 fail loud。</summary>
    internal const int SchemaVersion = 1;

    private const string StagingHomePrefix = ".tx-";
    private const string RollbackDirPrefix = ".rollback-";

    private sealed record PendingRecord(
        int SchemaVersion,
        string StagingHome,
        string StagingProfile,
        string RollbackDir,
        ProfileTxStep Step);

    private static readonly string[] s_copiedFileExclusions =
    {
        // 运行时管理文件不属于 profile 内容：staging 副本不携带（探针会在 staging 里另起 dsh web 写自己的）
        ".dsh-web-port",
        ".dsh-pid",
    };

    private readonly string _dshHome;
    private readonly Action<string> _log;
    private readonly string _pendingPath;
    private readonly string _rollbackDir;

    /// <summary>staging home 根（<c>DSH_HOME</c> 指向它跑 staged 变更/探针）。</summary>
    public string StagingHome { get; }

    /// <summary>staging 里的桌面 profile 目录（<c>--profile</c> 不变、home 换 staging）。</summary>
    public string StagingProfileDir { get; }

    private PluginProfileTransaction(string dshHome, string stagingHome, Action<string> log)
    {
        _dshHome = dshHome;
        _log = log;
        StagingHome = stagingHome;
        StagingProfileDir = Path.Combine(stagingHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        _pendingPath = Path.Combine(dshHome, "profiles", PendingFileName);
        _rollbackDir = Path.Combine(dshHome, "profiles", $"{RollbackDirPrefix}{Guid.NewGuid():N}");
    }

    /// <summary>
    /// 开始一次事务：整目录拷贝 active 桌面 profile 到 staging home（排除端口/PID 等运行时管理文件）。
    /// active profile 不存在时抛 <see cref="InvalidOperationException"/>——调用链保证 EnsureProfile 先行，
    /// 缺 profile 说明调用序错了，应当炸出来而不是静默降级。
    /// </summary>
    /// <param name="dshHome">共享 DSH_HOME。</param>
    /// <param name="log">诊断日志出口（host.log 同款行文）。</param>
    /// <returns>事务句柄；用完 <see cref="Discard"/> 或 <see cref="Activate"/> 收口。</returns>
    public static PluginProfileTransaction Begin(string dshHome, Action<string> log)
    {
        string activeProfile = Path.Combine(dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        if (!Directory.Exists(activeProfile))
        {
            throw new InvalidOperationException($"事务 Begin 失败：active profile 不存在（{activeProfile}）；调用链必须先 EnsureProfile");
        }

        string stagingHome = Path.Combine(dshHome, $"{StagingHomePrefix}{Guid.NewGuid():N}");
        var tx = new PluginProfileTransaction(dshHome, stagingHome, log);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            CopyDirectory(activeProfile, tx.StagingProfileDir);
        }
        catch
        {
            // 拷贝半途失败：staging 不完整且句柄尚未交出，就地收口后重抛（残留等 recover stray 清扫是兜底而非依赖）
            TryDeleteDir(stagingHome, "staging home", log);
            throw;
        }

        sw.Stop();
        log($"[host] 插件事务：staging 就绪（{tx.StagingProfileDir}，拷贝 {sw.ElapsedMilliseconds}ms）");
        return tx;
    }

    /// <summary>
    /// 提交：写 pending journal 后两步 rename 换入（active→rollback、staging→active），删 journal 收口。
    /// 中断语义：第一步 rename 前崩溃 = active 原封不动；两步之间崩溃 = journal 记 ActiveMoved，
    /// 启动 recover 重放；第二步后崩溃 = journal 记 StagingActivated，recover 只清尾巴。
    /// </summary>
    public void Activate()
    {
        string activeProfile = Path.Combine(_dshHome, "profiles", HarnessRuntimeHost.DesktopProfileName);
        WritePending(new PendingRecord(SchemaVersion, StagingHome, StagingProfileDir, _rollbackDir, ProfileTxStep.Prepared));
        Directory.Move(activeProfile, _rollbackDir);
        WritePending(new PendingRecord(SchemaVersion, StagingHome, StagingProfileDir, _rollbackDir, ProfileTxStep.ActiveMoved));
        Directory.Move(StagingProfileDir, activeProfile);
        WritePending(new PendingRecord(SchemaVersion, StagingHome, StagingProfileDir, _rollbackDir, ProfileTxStep.StagingActivated));
        File.Delete(_pendingPath);

        TryDeleteDir(_rollbackDir, "rollback");
        TryDeleteDir(StagingHome, "staging home");
        _log("[host] 插件事务：已换入 active（旧 profile 已让位回收）");
    }

    /// <summary>放弃事务：删 staging home，active 全程未被触碰。</summary>
    public void Discard()
    {
        TryDeleteDir(StagingHome, "staging home");
        _log("[host] 插件事务：已放弃（staging 清除，active 保持旧态）");
    }

    /// <summary>
    /// 启动期 recover（spawn 前调用）：有 pending journal 则按 step 重放或回滚，无则清残留
    /// （崩溃留下的 stray <c>.tx-*</c> staging home 与 <c>.rollback-*</c> 目录）。
    /// journal 损坏/版本不认识 = fail loud（抛 <see cref="InvalidOperationException"/>），
    /// 绝不静默清理——静默会把「active 缺位」的窗口态当无事发生。
    /// </summary>
    public static void Recover(string dshHome, Action<string> log)
    {
        string profilesRoot = Path.Combine(dshHome, "profiles");
        string pendingPath = Path.Combine(profilesRoot, PendingFileName);
        if (File.Exists(pendingPath))
        {
            PendingRecord record;
            try
            {
                record = JsonSerializer.Deserialize<PendingRecord>(File.ReadAllText(pendingPath))
                    ?? throw new JsonException("journal 为空");
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                throw new InvalidOperationException($"插件事务 journal 损坏（{pendingPath}）：{ex.Message}——fail loud，不静默清理；可手动删除该文件恢复", ex);
            }

            if (record.SchemaVersion != SchemaVersion)
            {
                throw new InvalidOperationException($"插件事务 journal 版本不认识（{record.SchemaVersion} != {SchemaVersion}）——fail loud，不静默清理；可手动删除该文件恢复（{pendingPath}）");
            }

            Replay(record, pendingPath, log);
        }

        CleanupStrays(dshHome, pendingPath, log);
    }

    private static void Replay(PendingRecord record, string pendingPath, Action<string> log)
    {
        switch (record.Step)
        {
            case ProfileTxStep.Prepared:
                string preparedActive = Path.Combine(
                    Path.GetDirectoryName(pendingPath)!, HarnessRuntimeHost.DesktopProfileName);
                if (Directory.Exists(record.RollbackDir))
                {
                    // 崩溃窗口：active→rollback 的 rename 已发生、journal 写 ActiveMoved 前被 kill——
                    // journal 还停在 Prepared 但 active 已缺位，必须按 rollback 回滚恢复（不能当「未动 active」）
                    if (Directory.Exists(preparedActive))
                    {
                        throw new InvalidOperationException(
                            $"插件事务 recover 失败：journal 在 Prepared 但 active 与 rollback 并存（{preparedActive} / {record.RollbackDir}）——状态无法判优，fail loud");
                    }

                    Directory.Move(record.RollbackDir, preparedActive);
                    TryDeleteDir(record.StagingHome, "staging home", log);
                    log("[host] 插件事务 recover：journal 在 Prepared 但 rollback 在位（rename 后断 journal），回滚恢复 active");
                    break;
                }

                // 还没动 active：staging 作废即可
                TryDeleteDir(record.StagingHome, "staging home", log);
                log("[host] 插件事务 recover：journal 在 Prepared，staging 作废、active 原封不动");
                break;
            case ProfileTxStep.ActiveMoved:
                string activeProfile = Path.Combine(
                    Path.GetDirectoryName(pendingPath)!, HarnessRuntimeHost.DesktopProfileName);
                if (Directory.Exists(record.StagingProfile))
                {
                    // 重放激活：staging 换入（与 Activate 后半段同序）
                    Directory.Move(record.StagingProfile, activeProfile);
                    log("[host] 插件事务 recover：journal 在 ActiveMoved，staging 重放换入 active");
                }
                else if (Directory.Exists(record.RollbackDir))
                {
                    // staging 已不在（被外力清掉）：回滚恢复旧 profile
                    Directory.Move(record.RollbackDir, activeProfile);
                    log("[host] 插件事务 recover：staging 缺失，rollback 回滚恢复 active");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"插件事务 recover 失败：ActiveMoved 但 staging 与 rollback 均缺失（{record.StagingProfile} / {record.RollbackDir}）——active 缺位，fail loud");
                }

                TryDeleteDir(record.RollbackDir, "rollback", log);
                TryDeleteDir(record.StagingHome, "staging home", log);
                break;
            case ProfileTxStep.StagingActivated:
                // 换入已完成：只剩尾巴清理
                TryDeleteDir(record.RollbackDir, "rollback", log);
                TryDeleteDir(record.StagingHome, "staging home", log);
                log("[host] 插件事务 recover：journal 在 StagingActivated，激活已完成、仅清尾巴");
                break;
            default:
                throw new InvalidOperationException($"插件事务 journal step 不认识（{record.Step}）——fail loud");
        }

        File.Delete(pendingPath);
    }

    private static void CleanupStrays(string dshHome, string pendingPath, Action<string> log)
    {
        // 无 pending 才清残留：有 pending 时 recover 主流程已按 step 处置对应目录，
        // 这里只兜「崩溃发生在 journal 写出之前/之后」的无主目录。
        if (File.Exists(pendingPath))
        {
            return;
        }

        foreach (string stagingHome in SafeEnumerateDirs(dshHome, StagingHomePrefix))
        {
            TryDeleteDir(stagingHome, "stray staging home", log);
        }

        foreach (string rollback in SafeEnumerateDirs(Path.Combine(dshHome, "profiles"), RollbackDirPrefix))
        {
            TryDeleteDir(rollback, "stray rollback", log);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string root, string prefix)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (string dir in Directory.EnumerateDirectories(root, $"{prefix}*"))
        {
            yield return dir;
        }
    }

    private static readonly JsonSerializerOptions s_pendingJsonOptions = new() { WriteIndented = true };

    private void WritePending(PendingRecord record) =>
        MarketInstallHelper.AtomicWriteFile(_pendingPath, JsonSerializer.Serialize(record, s_pendingJsonOptions));

    private void TryDeleteDir(string dir, string what) => TryDeleteDir(dir, what, _log);

    private static void TryDeleteDir(string dir, string what, Action<string>? log)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败只留日志：残留目录由下次 recover 的 stray 清扫兜底，不阻断主流程
            log?.Invoke($"[host] 插件事务：{what} 清理失败（留待下次 recover）：{ex.Message}");
        }
    }

    /// <summary>递归拷贝目录（排除运行时管理文件）。staging 与 active 同卷，后续 rename 原子。</summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            if (!s_copiedFileExclusions.Contains(Path.GetFileName(file)))
            {
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }
}
