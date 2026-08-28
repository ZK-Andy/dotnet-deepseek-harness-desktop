using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// active-run 崩溃取证 marker（ADR shell-observability-diagnostics，模式对齐 anywhere-labs
/// crash-evidence）：启动时原子写 <c>&lt;home&gt;/logs/run-marker.json</c>；正常退出仅 owner
/// （token 匹配）清除；下次启动发现遗留 marker 即判定上轮非受控退出。已存在 marker 若是
/// 符号链接/重解析点一律删除重建——绝不穿链接读写。
/// 已知取舍：同 home 多实例（dev+正式版并存）共用单一 marker，误报窗口接受。
/// </summary>
public static class RunMarker
{
    /// <summary>marker 文件相对 home 的路径。</summary>
    public static string MarkerPath(string home) => Path.Combine(home, "logs", "run-marker.json");

    /// <summary>启动占位。</summary>
    /// <param name="home">共享 DSH_HOME。</param>
    /// <returns>本轮 owner token；<see cref="RunMarkerResult.PreviousRunUnclean"/> 指示上轮是否非受控退出。</returns>
    public static RunMarkerResult Acquire(string home)
    {
        var dir = Path.Combine(home, "logs");
        Directory.CreateDirectory(dir);
        var path = MarkerPath(home);

        var previousUnclean = false;
        if (File.Exists(path))
        {
            previousUnclean = true;
            // 符号链接/重解析点不穿写：直接拔除再重建（防经链接写到 marker 语义之外）
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                File.Delete(path);
                HostLog.Write("[host] run-marker 为链接文件，已移除重建");
            }
        }

        var token = Guid.NewGuid().ToString("N");
        var temp = path + $".tmp-{token}";
        // 经 AppJsonContext 源生成（AOT 安全）；Release 读方只认 token 键
        var json = JsonSerializer.Serialize(
            new MarkerFile(token, Environment.ProcessId, DateTimeOffset.Now),
            AppJsonContext.Default.MarkerFile);
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
        return new RunMarkerResult(previousUnclean, token);
    }

    /// <summary>
    /// 正常退出清理。仅当现存 marker 的 token 与本实例匹配才删除——
    /// 不匹配说明已被后启动的实例接管（或已不存在），保持不动。
    /// </summary>
    public static bool Release(string home, string token)
    {
        try
        {
            var path = MarkerPath(home);
            if (!File.Exists(path))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var existing = doc.RootElement.TryGetProperty("token", out var t) &&
                           t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            if (existing != token)
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 清理失败不影响退出路径：下轮启动会按「非受控退出」处理并自愈
            Services.HostLog.Write($"[host] run-marker 清理失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>非受控退出提示横幅脚本（纯函数可单测）：不暗示应用故障，引导导出诊断。</summary>
    public static string UncleanBannerScript()
    {
        return DesktopBanner.Build(
            "dsh-desktop-run-marker-banner",
            "上次运行未正常退出（如手动结束进程）。若应用行为异常，请在设置页导出诊断信息。",
            new DesktopBanner.Palette("#1b1b26", "#e6e6ea", "#2a2a3a", "#7c3aed"));
    }

    /// <summary>run-marker.json 落盘帧；internal 供 <see cref="AppJsonContext"/> 源生成注册。</summary>
    /// <param name="Token">本轮 owner token（Release 清理凭据）。</param>
    /// <param name="Pid">进程号（取证线索）。</param>
    /// <param name="StartedAt">启动时刻（ISO 8601 round-trip 形态）。</param>
    internal sealed record MarkerFile(string Token, int Pid, DateTimeOffset StartedAt);
}

/// <summary><see cref="RunMarker.Acquire"/> 的结果。</summary>
/// <param name="PreviousRunUnclean">上轮是否遗留 marker（非受控退出）。</param>
/// <param name="Token">本轮 owner token，正常退出时交回 <see cref="RunMarker.Release"/>。</param>
public sealed record RunMarkerResult(bool PreviousRunUnclean, string Token);
