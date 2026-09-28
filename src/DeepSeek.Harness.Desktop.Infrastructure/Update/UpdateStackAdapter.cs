using System.Runtime.InteropServices;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Update;

/// <summary>
/// 自更新栈适配器（ADR update-coordinator-core-port）：Core 四端口（<see cref="IUpdateEnvironment"/>/
/// <see cref="IReleaseFeed"/>/<see cref="IPackageDownloader"/>/<see cref="IPackageInstaller"/>）的
/// Infrastructure 实现——宿主事实、HTTP 抓取与安装器派生收口于此，Presentation 层不再直连栈类型。
/// 共享 HttpClient 懒构造（无整体超时——feed 抓取与安装包下载各自经调用方限时，整体超时会误杀分钟级下载）。
/// </summary>
/// <param name="log">日志回调（可选；与协调器同款 host.log 行文出口）。</param>
public sealed class UpdateStackAdapter(Action<string>? log = null) :
    IUpdateEnvironment, IReleaseFeed, IPackageDownloader, IPackageInstaller
{
    private readonly Lazy<HttpClient> _http = new(UpdateHttpClient.Create);

    private HttpClient Http => _http.Value;

    /// <inheritdoc/>
    public bool IsEnabled(bool isDev) =>
        UpdateOptions.IsEnabledFor(isDev, Environment.GetEnvironmentVariable(UpdateOptions.ForceDevEnv));

    /// <inheritdoc/>
    public UpdateOptions LoadOptions() => LoadUpdateSection(AppContext.BaseDirectory);

    /// <summary>从应用旁的 appsettings.json 读取 <c>Update</c> 节；文件缺失或节缺失时全默认；
    /// 配置损坏不阻塞启动：回退全默认（fail-safe 而非 fail-loud——更新是增强功能）。</summary>
    private static UpdateOptions LoadUpdateSection(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new UpdateOptions();
        }

        try
        {
            return UpdateOptions.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return new UpdateOptions();
        }
    }

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
    public Task<ReleaseMeta?> FetchLatestAsync(UpdateOptions options, string rid, string? packageKind, CancellationToken cancellationToken) =>
        new ReleaseMetaClient(Http, options, log).FetchLatestAsync(rid, packageKind, cancellationToken);

    /// <inheritdoc/>
    public Task<string> DownloadAsync(ReleaseMeta meta, string destDir, TimeSpan timeout, CancellationToken cancellationToken) =>
        new InstallerDownloader(Http, log).DownloadAsync(meta, destDir, timeout, cancellationToken);

    /// <inheritdoc/>
    public Task<string> FetchExpectedSha256Async(string repository, string version, string assetName, CancellationToken cancellationToken) =>
        new InstallerDownloader(Http, log).FetchSha256Async(repository, version, assetName, cancellationToken);

    /// <inheritdoc/>
    public Task LaunchAsync(string assetPath, string workDir, string expectedSha256, TimeSpan observeWindow, CancellationToken cancellationToken) =>
        UpdateInstaller.LaunchAsync(assetPath, workDir, expectedSha256, observeWindow, cancellationToken, log: log);

    /// <summary>当前平台的更新资产 RID（与 release 资产命名后缀对应）。</summary>
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
