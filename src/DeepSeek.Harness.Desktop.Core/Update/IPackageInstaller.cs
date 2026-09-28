namespace DeepSeek.Harness.Desktop.Core.Update;

/// <summary>
/// 安装器端口（R3：接口在 Core、实现在 Infrastructure，ADR update-coordinator-core-port）：
/// 安装器派生与授权观察窗（脚本构造/提权细节在边界实现；成功路径进程随安装流程退出）。
/// </summary>
public interface IPackageInstaller
{
    /// <summary>校验后派生安装器并等待授权观察窗；失败抛出由状态机回退 ready。</summary>
    Task LaunchAsync(string assetPath, string workDir, string expectedSha256, TimeSpan observeWindow, CancellationToken cancellationToken);
}
