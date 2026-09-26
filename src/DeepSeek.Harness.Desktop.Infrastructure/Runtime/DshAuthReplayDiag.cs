namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>诊断分支（dsh 认证链 HTTP 级复演）：macOS 401 墙 M/R 二选一实验，用完即删。
/// dsh 铸币链（`/?token` → 303 + `Set-Cookie` → `/` 凭 cookie 200）在 curl 下自证完好，
/// WebView 侧恒 401。复演用托管 <see cref="HttpClient"/>（禁自动重定向、禁自动 cookie）
/// 对同一 URL 走完全相同的三步，判定 token 是被拒（R：服务端就没铸币）还是 cookie 在
/// WebView 侧丢了（M：复演 200 而 WebView 401）。
/// 环境变量 <c>DSH_DESKTOP_DIAG_HTTP_REPLAY=1</c> 开启；默认关闭零行为变更。
/// 日志只记状态码/头名/字节数与结论行，token 与 cookie 值永不落盘（单测钉死）。</summary>
internal static class DshAuthReplayDiag
{
    /// <summary>开启复演的环境变量名（CI 冒烟脚本注入；默认关闭）。</summary>
    internal const string EnableEnv = "DSH_DESKTOP_DIAG_HTTP_REPLAY";

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>复演是否开启。</summary>
    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnableEnv), "1", StringComparison.Ordinal);

    /// <summary>单步结果：状态码 + Set-Cookie 名（无值）+ 原始名值（仅组请求头用，永不记日志）+ Location + 体字节数。</summary>
    /// <param name="Status">HTTP 状态码。</param>
    /// <param name="CookieNames">Set-Cookie 的名（等号之前，无值，可记日志）。</param>
    /// <param name="RawCookies">原始名值对（`名=值`，无属性；仅组装 Cookie 请求头，禁止记日志）。</param>
    /// <param name="Location">Location 头值（可空）。</param>
    /// <param name="Bytes">响应体字节数。</param>
    internal sealed record StepResult(int Status, List<string> CookieNames, List<string> RawCookies, string? Location, long Bytes);

    /// <summary>复演三步并留痕；任何失败只 loud 一行，永不抛（诊断不得阻断启动/退出）。</summary>
    /// <param name="url">dsh web 完整 URL（含 token，仅用于发请求，永不记日志）。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="ct">取消令牌：应用退出即静默收工。</param>
    internal static async Task ReplayAsync(Uri url, Action<string> log, CancellationToken ct)
    {
        string origin = url.GetLeftPart(UriPartial.Authority);
        string? token = TryGetToken(url);
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = s_stepTimeout,
            };
            StepResult bare = await GetAsync(client, origin + "/", null, ct).ConfigureAwait(false);
            log($"[diag] replay: 裸 / → {bare.Status}（{bare.Bytes}B；{origin} 可达性）");
            StepResult mint = await GetAsync(client, url.ToString(), null, ct).ConfigureAwait(false);
            log($"[diag] replay: token 跳 → {mint.Status}（location={Redact(mint.Location ?? "无", token)}；set-cookie=[{string.Join(",", mint.CookieNames)}] 共{mint.CookieNames.Count}个）");
            bool minted = mint.Status == 303 && mint.CookieNames.Count > 0;
            string followup = "skip";
            if (minted)
            {
                string cookieHeader = string.Join("; ", mint.RawCookies);
                StepResult authed = await GetAsync(client, origin + "/", cookieHeader, ct).ConfigureAwait(false);
                log($"[diag] replay: 凭 cookie 再访 / → {authed.Status}（{authed.Bytes}B；送出 {mint.CookieNames.Count} 个 cookie 名）");
                followup = authed.Status.ToString();
            }

            log($"[diag] replay 结论：mint={(minted ? "yes" : "no")}；cookie-followup={followup}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用退出：静默收工，不阻断。
        }
        catch (Exception ex)
        {
            // 异常消息可能含请求 URI（token）：脱敏后 loud，永不抛。
            log($"[diag] replay 失败（不阻断启动）：{ex.GetType().Name} {Redact(ex.Message, token)}");
        }
    }

    /// <summary>单步 GET：状态码 + Set-Cookie 名/原始值 + Location + 体长。值永不记日志。</summary>
    internal static async Task<StepResult> GetAsync(HttpClient client, string requestUrl, string? cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        List<string> names = [];
        List<string> raw = [];
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
        {
            foreach (string sc in setCookies)
            {
                int eq = sc.IndexOf('=');
                int semi = sc.IndexOf(';');
                names.Add(eq > 0 ? sc[..eq] : sc);
                raw.Add(eq > 0 ? sc[..(semi > 0 ? semi : sc.Length)] : sc);
            }
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new StepResult((int)response.StatusCode, names, raw, response.Headers.Location?.ToString(), body.LongLength);
    }

    /// <summary>从 URL 取 token 查询值（段级解析，token 不必首参；日志脱敏用；取不到返回 null）。</summary>
    internal static string? TryGetToken(Uri url)
    {
        string query = url.Query;
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        const string prefix = "token=";
        foreach (string segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith(prefix, StringComparison.Ordinal))
            {
                return segment[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>脱敏：消息中的 token 子串替换为 ***（无 token 时原样返回）。</summary>
    internal static string Redact(string message, string? token) =>
        string.IsNullOrEmpty(token) ? message : message.Replace(token, "***", StringComparison.Ordinal);
}
