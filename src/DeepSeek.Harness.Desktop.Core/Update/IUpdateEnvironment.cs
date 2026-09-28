namespace DeepSeek.Harness.Desktop.Core.Update;

/// <summary>
/// 自更新宿主环境端口（R3：接口在 Core、实现在 Infrastructure，ADR update-coordinator-core-port）：
/// 协调器装载与对账所需的宿主侧事实——dev 门禁、配置装载、更新目录、平台判定、启动对账清扫与
/// ready 持久化工厂。端口只收敛协调器的消费集，不追求宿主全貌（MVP 判据）。
/// </summary>
public interface IUpdateEnvironment
{
    /// <summary>是否装载自更新栈：非 dev 恒真；dev 需显式 <c>DSH_DESKTOP_UPDATE_FORCE=1</c>（环境变量读取在边界实现）。</summary>
    bool IsEnabled(bool isDev);

    /// <summary>从应用旁的 appsettings.json 读取 <c>Update</c> 节（文件缺失或节缺失时全默认）。</summary>
    UpdateOptions LoadOptions();

    /// <summary>当前应用版本（入口程序集 InformationalVersion——宿主事实，随边界注入免测试宿主版本串扰）。</summary>
    string CurrentVersion();

    /// <summary>解析更新目录（<c>DSH_HOME</c> 下的 ready 持久化与安装包目录）。</summary>
    string ResolveUpdatesDir(string updatesDirName);

    /// <summary>当前平台的更新资产 RID（与 release 资产命名后缀对应）。</summary>
    string UpdateRid();

    /// <summary>当前包类型（deb/rpm），检测不到为 null；win/mac 忽略。</summary>
    string? DetectPackageKind();

    /// <summary>启动对账清扫：删过期安装包与 install.sh/.download.lock 死残留（ADR self-update-prune-consumed-packages）。</summary>
    void PruneStale(string updatesDir, string currentVersion);

    /// <summary>创建 ready 记录持久化（状态机 <see cref="UpdateStateMachine.IPersistence"/> 的宿主实现）。</summary>
    UpdateStateMachine.IPersistence CreateReadyPersistence(string updatesDir);
}
