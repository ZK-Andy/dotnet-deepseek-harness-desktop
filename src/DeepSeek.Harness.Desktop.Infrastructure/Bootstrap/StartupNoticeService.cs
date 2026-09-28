namespace DeepSeek.Harness.Desktop.Infrastructure.Bootstrap;

/// <summary>
/// 启动期告知服务（原编排器 <c>SharedHomeBannerTask</c> 的用例编排随细搬下沉，ADR
/// post-packaging-churn-restructure 余批）：dsh 版本底线检查、旧 home 一次性提示、上轮非受控退出
/// 横幅。判定（探测/底线/旧 home 留痕）在本层；横幅构建与推送是展示面，经委托闭包接线。
/// </summary>
public sealed class StartupNoticeService
{
    private readonly IFirstBootBootstrap _bootstrap;
    private readonly TimeSpan _settleTimeout;
    private readonly bool _previousRunUnclean;
    private readonly Action<string> _log;
    private readonly Func<string, CancellationToken, Task> _showVersionFloorBanner;
    private readonly Func<CancellationToken, Task> _showUncleanExitBanner;

    /// <summary>创建启动期告知服务。</summary>
    /// <param name="bootstrap">首启引导端口（落定握手：引导未落定前横幅不抢跑）。</param>
    /// <param name="settleTimeout">落定等待上限（超时按已定继续，降级语义在 BootstrapSettleGate 内）。</param>
    /// <param name="previousRunUnclean">上轮是否非受控退出（崩溃取证 marker 判定）。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="showVersionFloorBanner">版本低于底线的横幅推送（接收探测到的版本；展示面构建脚本）。</param>
    /// <param name="showUncleanExitBanner">非受控退出横幅推送。</param>
    public StartupNoticeService(
        IFirstBootBootstrap bootstrap,
        TimeSpan settleTimeout,
        bool previousRunUnclean,
        Action<string> log,
        Func<string, CancellationToken, Task> showVersionFloorBanner,
        Func<CancellationToken, Task> showUncleanExitBanner)
    {
        _bootstrap = bootstrap;
        _settleTimeout = settleTimeout;
        _previousRunUnclean = previousRunUnclean;
        _log = log;
        _showVersionFloorBanner = showVersionFloorBanner;
        _showUncleanExitBanner = showUncleanExitBanner;
    }

    /// <summary>执行启动期告知（后台任务体；调用方以 fire-and-forget 启动）。取消即放弃未推送的告知。</summary>
    /// <param name="ct">监督器取消令牌（应用退出时中止等待与推送）。</param>
    public async Task RunAsync(CancellationToken ct)
    {
        // 共享 home 切换的启动期告知（ADR shared-home-desktop-profile）：版本底线检查 + 旧 home 一次性提示。
        // 随包插件现于 spawn dsh 前安装（不再「启动后装 → 覆写页面并重启运行时」），横幅无需等安装收尾，
        // 只需等首启引导落定——版本探针走 PATH 上全局 dsh（bundled=null），提前跑会探到空。
        // 引导落定前横幅不抢跑；超时按已定继续（降级语义在 BootstrapSettleGate 内），取消即放弃。
        if (!await _bootstrap.WaitSettledAsync(_settleTimeout, ct))
        {
            return;
        }

        string home = HarnessRuntimeHost.ResolveDshHome();
        string? detected = await RuntimeVersionGate.ProbeAsync(ct);
        if (detected is not null)
        {
            _log($"[host] dsh 版本 {detected}（底线 {RuntimeVersionGate.MinimumVersion}）");
            if (RuntimeVersionGate.IsBelowFloor(detected))
            {
                _log($"[host] 警告：dsh {detected} 低于支持底线 {RuntimeVersionGate.MinimumVersion}，已提示用户");
                await _showVersionFloorBanner(detected, ct);
            }
        }
        else
        {
            _log("[host] dsh 版本探测失败，跳过底线检查");
        }

        // 旧 home 留痕仅进日志（界面横幅已按用户拍板去除，ADR companion-settings-consolidation）；
        // 指回旧目录时不记「改用新目录」——自相矛盾且无信息量
        if (LegacyHomeNotice.IsPresent() && !PathIdentity.PathsEqual(home, LegacyHomeNotice.LegacyPrivateHome))
        {
            _log($"[host] 检测到旧版桌面数据目录 {LegacyHomeNotice.LegacyPrivateHome}；新版使用 {home}（未迁移）");
        }

        // 上轮非受控退出：提示但不暗示应用故障（用户杀进程也属此类），引导导出诊断
        if (_previousRunUnclean)
        {
            await _showUncleanExitBanner(ct);
        }
    }
}
