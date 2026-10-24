using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeepSeek.Harness.Desktop.Core.Platform;
using Microsoft.Win32;

namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// Windows WebView2 运行时探测（<see cref="IWebViewRuntimeProbe"/> 的 Infrastructure 实现；
/// ADR avalonia-g4-webview2-preflight）：复刻 Avalonia 控件 <c>ManagedWebView2Loader</c> 的解析次序——
/// <c>WEBVIEW2_BROWSER_EXECUTABLE_FOLDER</c> 覆盖优先，其次 HKLM 全部通道、再 HKCU 全部通道的
/// <c>ClientState\&lt;uuid&gt;\EBWebView</c> 目录，最终以 <c>EBWebView\&lt;arch&gt;\EmbeddedBrowserWebView.dll</c>
/// 存在为准；安装器的 <c>Clients\pv</c> 只记「装过」，不作判据。异常不在此吞掉，由 guard 统一 fail-open。
/// </summary>
internal sealed class WebView2RuntimeProbe : IWebViewRuntimeProbe
{
    /// <summary>注册表中安装目录的值名（与控件同源）。</summary>
    internal const string BrowserFolderValueName = "EBWebView";

    /// <summary>Edge WebView2 通道 UUID（Stable/Beta/Dev/Canary/Internal），与控件的通道顺序同源。</summary>
    private static readonly string[] s_channels =
    [
        "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        "{2CD8A007-E189-409D-A2C8-9AF4EF3C72AA}",
        "{0D50BFEC-CD6A-4F9A-964C-C7416E3ACB10}",
        "{65C35B14-6C1D-4122-AC46-7148CC9D6497}",
        "{BE59E8FD-089A-411B-A3B0-051D9E417818}",
    ];

    /// <summary>通道列表（只读；顺序与控件解析一致，测试逐字钉住）。</summary>
    internal static IReadOnlyList<string> Channels => s_channels;

    /// <inheritdoc />
    public bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return FindRuntime() is not null;
    }

    /// <summary>运行时 dll 路径（纯函数，与控件同布局）：<c>&lt;folder&gt;\EBWebView\&lt;arch&gt;\EmbeddedBrowserWebView.dll</c>。</summary>
    /// <param name="browserFolder">运行时安装目录（<c>EBWebView</c> 值的原文）；空值为 null。</param>
    /// <param name="architecture">进程架构。</param>
    /// <returns>dll 路径；目录为空时 null。</returns>
    internal static string? DllPath(string? browserFolder, Architecture architecture) =>
        string.IsNullOrEmpty(browserFolder)
            ? null
            : Path.Combine(browserFolder, "EBWebView", ArchFolder(architecture), "EmbeddedBrowserWebView.dll");

    /// <summary>进程架构 → 运行时子目录名；未知值按 x64（防御性缺省：控件只映射四值，出现新值即须跟版）。</summary>
    /// <param name="architecture">进程架构。</param>
    /// <returns>子目录名。</returns>
    internal static string ArchFolder(Architecture architecture) => architecture switch
    {
        Architecture.Arm => "arm",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>通道的注册表子键（与控件同源）。</summary>
    /// <param name="channel">通道 UUID。</param>
    /// <returns><c>Software\Microsoft\EdgeUpdate\ClientState\&lt;uuid&gt;</c>。</returns>
    internal static string ClientStateKey(string channel) => $@"Software\Microsoft\EdgeUpdate\ClientState\{channel}";

    /// <summary>按控件同源判据找运行时 dll（env 覆盖 → HKLM 全通道 → HKCU 全通道）。</summary>
    /// <returns>dll 绝对路径；未找到为 null。</returns>
    [SupportedOSPlatform("windows")]
    private static string? FindRuntime()
    {
        if (DllPath(Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"), RuntimeInformation.ProcessArchitecture) is { } overridePath
            && File.Exists(overridePath))
        {
            return overridePath;
        }

        return FirstExisting(RegistryHive.LocalMachine) ?? FirstExisting(RegistryHive.CurrentUser);
    }

    /// <summary>在单个注册表根下按通道顺序找运行时 dll；未找到为 null。</summary>
    [SupportedOSPlatform("windows")]
    private static string? FirstExisting(RegistryHive hive)
    {
        foreach (string channel in s_channels)
        {
            if (DllPath(ReadBrowserFolder(hive, channel), RuntimeInformation.ProcessArchitecture) is { } path
                && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>读通道安装目录（<c>ClientState\&lt;uuid&gt;\EBWebView</c>）；读不到返回 null。</summary>
    /// <param name="hive">注册表根。</param>
    /// <param name="channel">通道 UUID。</param>
    /// <returns>安装目录原文；缺失为 null。</returns>
    [SupportedOSPlatform("windows")]
    private static string? ReadBrowserFolder(RegistryHive hive, string channel)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
        using RegistryKey? key = baseKey.OpenSubKey(ClientStateKey(channel));
        return key?.GetValue(BrowserFolderValueName) as string;
    }
}
