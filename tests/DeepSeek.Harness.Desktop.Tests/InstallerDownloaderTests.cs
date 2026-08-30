using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// 下载器的 IO 边界行为：HTTP 非 2xx fail loud、SHA256SUMS 整体缺失拒装、
/// 哈希不匹配清理半成品、成功路径原子改名。
/// </summary>
public class InstallerDownloaderTests
{
    private const string Asset = "app_0.1.21_linux-amd64.deb";
    private const string AssetUrl = "https://example.test/releases/download/v0.1.21/app_0.1.21_linux-amd64.deb";
    private const string ShaUrl = "https://example.test/releases/download/v0.1.21/SHA256SUMS.txt";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static ReleaseMeta Meta(string? shaUrl = ShaUrl) => new("v0.1.21", Asset, AssetUrl, shaUrl);

    private static InstallerDownloader Downloader(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond));
        return new InstallerDownloader(http);
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AssetHttpError_ThrowsAndLeavesNoPart()
    {
        // GetStreamAsync 对非 2xx 不抛——404 必须显式转 HttpRequestException，且不留 .part 半成品
        string dir = TempDir();
        try
        {
            InstallerDownloader dl = Downloader(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("nope") });

            await Assert.ThrowsAsync<HttpRequestException>(
                () => dl.DownloadAsync(Meta(), dir, TimeSpan.FromSeconds(30), CancellationToken.None));

            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MissingShaFile_RefusesToInstall()
    {
        // release 未附 SHA256SUMS.txt：宁可拒装不装坏包（ADR 强校验立场）
        string dir = TempDir();
        try
        {
            InstallerDownloader dl = Downloader(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("payload") });

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => dl.DownloadAsync(Meta(shaUrl: null), dir, TimeSpan.FromSeconds(30), CancellationToken.None));

            Assert.Contains("SHA256SUMS", ex.Message);
            // .download.lock 设计上常驻（dispose 释放句柄即可），只断言无半成品
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HashMismatch_ThrowsAndDeletesPart()
    {
        string dir = TempDir();
        try
        {
            InstallerDownloader dl = Downloader(req =>
            {
                if (req.RequestUri!.AbsoluteUri == ShaUrl)
                {
                    string wrong = new('a', 64);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{wrong}  {Asset}\n") };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("payload") };
            });

            await Assert.ThrowsAsync<InvalidDataException>(
                () => dl.DownloadAsync(Meta(), dir, TimeSpan.FromSeconds(30), CancellationToken.None));

            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MissingSumEntry_Throws()
    {
        string dir = TempDir();
        try
        {
            InstallerDownloader dl = Downloader(req =>
            {
                if (req.RequestUri!.AbsoluteUri == ShaUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{"a".PadRight(64, 'b')}  other.deb\n") };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("payload") };
            });

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => dl.DownloadAsync(Meta(), dir, TimeSpan.FromSeconds(30), CancellationToken.None));

            Assert.Contains("无", ex.Message);
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Success_MovesAtomically_AndCleansPart()
    {
        string dir = TempDir();
        try
        {
            const string payload = "PAYLOAD-CONTENT";
            InstallerDownloader dl = Downloader(req =>
            {
                if (req.RequestUri!.AbsoluteUri == ShaUrl)
                {
                    string hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hex}  {Asset}\n") };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
            });

            string dest = await dl.DownloadAsync(Meta(), dir, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(Path.Combine(dir, Asset), dest);
            Assert.Equal(payload, File.ReadAllText(dest));
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
