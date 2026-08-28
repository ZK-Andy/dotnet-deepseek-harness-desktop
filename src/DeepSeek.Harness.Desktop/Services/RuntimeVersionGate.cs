using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 启动版本底线检查（只读探测，ADR shared-home-desktop-profile）：共享 home 后桌面钉版运行时与
/// 用户自管 CLI 写同一 home，版本偏斜的唯一防线——探测即将执行的 dsh 版本，低于底线仅明确提示，
/// 不阻断、不做迁移管控。探测失败（超时/进程失败/不可解析）视为未知：只记日志，不用横幅打扰。
/// </summary>
public static class RuntimeVersionGate
{
    /// <summary>
    /// 桌面支持的最低 dsh 版本（与随包闭包钉版同源：<c>bundle-runtime-ci.sh</c> 的 DSH_VERSION
    /// 升级时同步此常量）。协议级兼容底线，固定在代码不入 appsettings。
    /// </summary>
    public const string MinimumVersion = "0.1.1-rc.2";

    /// <summary>版本探测时限：<c>dsh --version</c> 是毫秒级调用，超时按未知处理而非阻塞启动。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private static readonly Regex VersionToken = new(
        @"v?\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>从 <c>--version</c> 输出提取首个版本 token（如 <c>0.1.1-rc.2</c>，容忍 <c>v</c> 前缀）；无匹配返回 null。</summary>
    public static string? TryParseVersionOutput(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var match = VersionToken.Match(line.Trim());
            if (match.Success)
            {
                return match.Value.TrimStart('v', 'V');
            }
        }

        return null;
    }

    /// <summary>
    /// 是否低于底线。数字段逐段比较（<see cref="Update.UpdateVersion.Compare"/>），预发布后缀不参与——
    /// 同数字核内 rc.1 与 rc.2 视为同级；粗粒度足够：防线目标是拦截跨 minor 的老运行时。
    /// </summary>
    public static bool IsBelowFloor(string version) =>
        Update.UpdateVersion.Compare(version, MinimumVersion) < 0;

    /// <summary>低于底线时的界面横幅注入脚本（纯函数可单测）：告知探测版本、底线与后果。</summary>
    public static string BelowFloorBannerScript(string detectedVersion)
    {
        var text = "当前 dsh 运行时版本 " + detectedVersion +
                   " 低于桌面支持的最低版本 " + MinimumVersion +
                   "，可能出现数据或行为不兼容；请升级 dsh，或使用桌面自带的捆绑运行时。";
        return DesktopBanner.Build(
            "dsh-desktop-version-floor-banner",
            text,
            new DesktopBanner.Palette("#3a1d1d", "#ffe6e6", "#5a2a2a", "#a13a3a"));
    }

    /// <summary>
    /// 只读探测 dsh 版本：bundled 给定时执行 <c>node bin.js --version</c>，否则 PATH <c>dsh --version</c>。
    /// </summary>
    /// <returns>探测到的版本串；超时、进程失败或输出不可解析返回 null（未知 ≠ 不合格，不提示横幅）。</returns>
    public static async Task<string?> ProbeAsync((string NodeExe, string DshEntry)? bundled, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (bundled is { } b)
            {
                psi.FileName = b.NodeExe;
                psi.ArgumentList.Add(b.DshEntry);
            }
            else
            {
                psi.FileName = "dsh";
            }

            psi.ArgumentList.Add("--version");

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 dsh --version 进程");
            var stdout = await p.StandardOutput.ReadToEndAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return TryParseVersionOutput(stdout);
        }
        catch (OperationCanceledException)
        {
            // 探测超时或应用退出导致取消：按未知处理，不阻断启动链路
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // dsh 缺失/不可执行：真正的启动失败由 StartAsync → 降级 wwwroot 链路呈现，这里只放弃探测
            HostLog.Write($"[host] dsh 版本探测失败：{ex.Message}");
            return null;
        }
    }
}
