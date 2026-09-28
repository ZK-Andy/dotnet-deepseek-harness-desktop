namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>
/// 代理源头改写策略（安全不变量单源，原 DshShellForward/DshLoopbackTunnel 双写收口，ADR
/// restructure-proxy-header-policy）：页源零透传——页面侧提交的 Origin/Referer 一律剔除，
/// 两个出口面（HttpClient 转发 <c>DshShellForward.BuildForwardRequest</c>、裸 TCP 隧道
/// <c>DshLoopbackTunnel.BuildUpgradeHead</c>）统一改写为 dsh 自源形态（与 dsh 直出同形态；
/// 页源代理 URL 在 dsh 网关处即外源 403，dispatch 实证）。dsh 网关口径漂移时只改此一处。
/// </summary>
internal static class ProxyHeaderPolicy
{
    /// <summary>页面侧源头头名（Origin/Referer，大小写不敏感）：请求构造面必须剔除页值并按
    /// <see cref="SelfSource"/> 改写——页值零透传是不变量本体，不区分值是否「看起来无害」。</summary>
    /// <param name="name">请求头名（原样大小写）。</param>
    /// <returns>true = 该头由改写面接管，页值不得透传。</returns>
    public static bool IsPageSourceHeader(string name) =>
        name.Equals("origin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("referer", StringComparison.OrdinalIgnoreCase);

    /// <summary>dsh 自源形态（唯一事实源）：Origin = dsh authority（scheme://host:port）；
    /// Referer = authority + 原样 path/query——与 dsh 直出时浏览器产生的源头形态逐字节一致。</summary>
    /// <param name="dshAuthority">dsh 路由 authority（含 scheme 与端口）。</param>
    /// <param name="dshPathAndQuery">原样 path（以 / 起，可带 query；无路径时为 /）。</param>
    /// <returns>Origin/Referer 改写值对。</returns>
    public static (string Origin, string Referer) SelfSource(string dshAuthority, string dshPathAndQuery) =>
        (dshAuthority, dshAuthority + dshPathAndQuery);
}
