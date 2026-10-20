using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>
/// 假系统浏览器（<see cref="ISystemBrowser"/> 端口替身）：记录被请求打开的 URL，
/// 可指定打开结果或模拟浏览器不可用（抛异常）。平台面在 Infrastructure，测试不碰真实浏览器。
/// </summary>
internal sealed class FakeSystemBrowser : ISystemBrowser
{
    private readonly List<string> _opened = new();
    private readonly bool _result;
    private readonly bool _throwOnOpen;

    /// <summary>创建替身。</summary>
    /// <param name="result">打开是否成功（默认成功）。</param>
    /// <param name="throwOnOpen">打开时抛异常（模拟浏览器不可用）。</param>
    public FakeSystemBrowser(bool result = true, bool throwOnOpen = false)
    {
        _result = result;
        _throwOnOpen = throwOnOpen;
    }

    /// <summary>被请求打开的 URL（按调用序）。</summary>
    public IReadOnlyList<string> Opened => _opened;

    /// <inheritdoc />
    public bool Open(string url)
    {
        _opened.Add(url);
        if (_throwOnOpen)
        {
            throw new InvalidOperationException("no browser");
        }

        return _result;
    }
}
