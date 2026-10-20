using System.Runtime.InteropServices;

namespace DeepSeek.Harness.Desktop.Infrastructure.Update;

/// <summary>
/// 自更新宿主环境适配器（Core 端口 <see cref="IUpdateEnvironment"/> 的 Infrastructure 实现）：
/// dev 门禁、配置装载、更新目录、平台判定、启动对账清扫与 ready 持久化工厂——纯宿主事实，不触网络。
/// </summary>
/// <param name="log">日志回调（可选；与协调器同款 host.log 行文出口）。</param>
public sealed class UpdateEnvironmentAdapter(Action<string>? log = null) : IUpdateEnvironment
{
    /// <inheritdoc/>
    public bool IsEnabled(bool isDev) =>
        UpdateOptions.IsEnabledFor(isDev, Environment.GetEnvironmentVariable(UpdateOptions.ForceDevEnv));

    /// <inheritdoc/>
    public UpdateOptions LoadOptions() => LoadUpdateSection(AppContext.BaseDirectory);

    /// <summary>从应用旁的 appsettings.json 读取 <c>Update</c> 节；文件缺失或节缺失时全默认；
    /// 配置损坏/不可读不阻塞启动：回退全默认并留痕（fail-safe 而非 fail-loud——更新是增强功能）。</summary>
    private UpdateOptions LoadUpdateSection(string baseDirectory) =>
        ConfigSectionFile.LoadFile(baseDirectory, "Update", UpdateOptions.Parse, new UpdateOptions(), log);

    /// <inheritdoc/>
    public string CurrentVersion() => AppVersion.Current();

    /// <inheritdoc/>
    public string ResolveUpdatesDir(string updatesDirName) =>
        Path.Combine(HarnessRuntimeHost.ResolveDshHome(), updatesDirName);

    /// <inheritdoc/>
    public string? DetectPackageKind() => UpdatePlatform.DetectCurrentPackageKind();

    /// <inheritdoc/>
    public void PruneStale(string updatesDir, string currentVersion) =>
        StalePackagePruner.Run(updatesDir, currentVersion, log: log);

    /// <inheritdoc/>
    public UpdateStateMachine.IPersistence CreateReadyPersistence(string updatesDir) =>
        new FileReadyPersistence(updatesDir);

    /// <inheritdoc/>
    public string UpdateRid()
    {
        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }

        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        return "unknown";
    }
}
