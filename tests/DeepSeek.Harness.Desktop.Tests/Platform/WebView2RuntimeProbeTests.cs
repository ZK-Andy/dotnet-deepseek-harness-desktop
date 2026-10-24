using System.Runtime.InteropServices;

namespace DeepSeek.Harness.Desktop.Tests.Platform;

/// <summary>
/// WebView2 运行时探测的纯布局面（ADR avalonia-g4-webview2-preflight）：dll 路径必须与
/// Avalonia 控件 <c>ManagedWebView2Loader</c> 同布局（<c>&lt;folder&gt;\EBWebView\&lt;arch&gt;\EmbeddedBrowserWebView.dll</c>），
/// 否则壳判定「缺失」而控件其实找得到（或反之），提示与实际行为就会分叉。
/// </summary>
public class WebView2RuntimeProbeTests
{
    /// <summary>进程架构映射到运行时子目录名。</summary>
    /// <param name="architecture">进程架构。</param>
    /// <param name="expected">期望子目录名。</param>
    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.X86, "x86")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.Arm, "arm")]
    public void ArchFolder_MapsProcessArchitecture(Architecture architecture, string expected) =>
        Assert.Equal(expected, WebView2RuntimeProbe.ArchFolder(architecture));

    /// <summary>未知架构按 x64 回退（与控件同一回退）。</summary>
    [Fact]
    public void ArchFolder_UnknownArchitecture_FallsBackToX64() =>
        Assert.Equal("x64", WebView2RuntimeProbe.ArchFolder((Architecture)99));

    /// <summary>dll 路径按控件的三段布局拼装。</summary>
    [Fact]
    public void DllPath_UsesLoaderLayout() =>
        Assert.Equal(
            Path.Combine("/opt/webview2", "EBWebView", "arm64", "EmbeddedBrowserWebView.dll"),
            WebView2RuntimeProbe.DllPath("/opt/webview2", Architecture.Arm64));

    /// <summary>安装目录缺失（未设 env / 注册表无值）时不给路径。</summary>
    /// <param name="folder">空值形态。</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DllPath_MissingFolder_ReturnsNull(string? folder) =>
        Assert.Null(WebView2RuntimeProbe.DllPath(folder, Architecture.X64));

    /// <summary>通道表与控件同源（Stable/Beta/Dev/Canary/Internal，顺序固定）。</summary>
    [Fact]
    public void Channels_MatchLoaderOrder() =>
        Assert.Equal(
            new[] { "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "{2CD8A007-E189-409D-A2C8-9AF4EF3C72AA}", "{0D50BFEC-CD6A-4F9A-964C-C7416E3ACB10}", "{65C35B14-6C1D-4122-AC46-7148CC9D6497}", "{BE59E8FD-089A-411B-A3B0-051D9E417818}" },
            WebView2RuntimeProbe.Channels);

    /// <summary>ClientState 子键模板与控件同源。</summary>
    [Fact]
    public void ClientStateKey_UsesEdgeUpdateClientState() =>
        Assert.Equal(
            @"Software\Microsoft\EdgeUpdate\ClientState\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            WebView2RuntimeProbe.ClientStateKey("{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"));

    /// <summary>安装目录值名固定为 EBWebView。</summary>
    [Fact]
    public void BrowserFolderValueName_IsEbWebView() =>
        Assert.Equal("EBWebView", WebView2RuntimeProbe.BrowserFolderValueName);
}
