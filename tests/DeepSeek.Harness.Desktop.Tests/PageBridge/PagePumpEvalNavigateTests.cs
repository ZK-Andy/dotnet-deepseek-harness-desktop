namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>首跳 eval 导航（ADR smoke-witness-real-and-eval-first-hop）：脚本纯函数与发出判据的回归——
/// 发出与否是 arm64 绕行/原生回退的分叉点，判错即绕行恒走空或回退恒不触发。</summary>
public class PagePumpEvalNavigateTests
{
    /// <summary>脚本把目标 URL 经 JSON 字面量拼入 <c>location.href</c>，同步返回布尔。</summary>
    [Fact]
    public void Script_AssignsLocationHrefAndReturnsBool()
    {
        string script = PagePump.EvalNavigateScript(new Uri("http://127.0.0.1:37933/"));

        Assert.Contains("location.href=\"http://127.0.0.1:37933/\"", script, StringComparison.Ordinal);
        Assert.Contains("return true;", script, StringComparison.Ordinal);
        Assert.Contains("return false;", script, StringComparison.Ordinal);
    }

    /// <summary>URL 含查询串（token）不断字：JSON 序列化保证引号转义，脚本不断裂。</summary>
    [Fact]
    public void Script_UrlWithTokenQuery_SerializesSafely()
    {
        string script = PagePump.EvalNavigateScript(new Uri("http://127.0.0.1:37933/?token=abc"));

        Assert.Contains("location.href=\"http://127.0.0.1:37933/?token=abc\"", script, StringComparison.Ordinal);
    }

    /// <summary>token 含引号不断裂：URL 经 JSON 字面量整体拼入，拼入形态恒为序列化输出。</summary>
    [Fact]
    public void Script_UrlWithQuoteInQuery_EmbedsSerializedForm()
    {
        var target = new Uri("http://127.0.0.1:37933/?token=a\"b");
        string expected = System.Text.Json.JsonSerializer.Serialize(target.ToString());

        string script = PagePump.EvalNavigateScript(target);

        Assert.Contains("location.href=" + expected, script, StringComparison.Ordinal);
    }

    /// <summary>页内返回真（桥 JSON 布尔形态）→ 已发出。</summary>
    [Fact]
    public async Task EvalReturnsTrue_IssuedAsync()
    {
        Task<bool> issued = PagePump.TryNavigateViaEvalAsync(
            (_, _) => new ValueTask<string>("true"),
            new Uri("http://127.0.0.1:37933/"),
            30,
            CancellationToken.None);

        Assert.True(await issued);
    }

    /// <summary>页内返回假（catch 分支）→ 未发出，调用方回退原生。</summary>
    [Fact]
    public async Task EvalReturnsFalse_NotIssuedAsync()
    {
        Task<bool> issued = PagePump.TryNavigateViaEvalAsync(
            (_, _) => new ValueTask<string>("false"),
            new Uri("http://127.0.0.1:37933/"),
            30,
            CancellationToken.None);

        Assert.False(await issued);
    }

    /// <summary>eval 悬空（桥回包到不了宿主）→ 有界超时变 false，不把调用方卡死。</summary>
    [Fact]
    public async Task EvalHangs_BoundedTimeoutNotIssuedAsync()
    {
        Task<bool> issued = PagePump.TryNavigateViaEvalAsync(
            (_, _) => new ValueTask<string>(new TaskCompletionSource<string>().Task),
            new Uri("http://127.0.0.1:37933/"),
            1,
            CancellationToken.None);

        Assert.False(await issued);
    }

    /// <summary>窗口未就绪（Current 抛）→ false，调用方回退原生。</summary>
    [Fact]
    public async Task EvaluatorThrows_NotIssuedAsync()
    {
        Task<bool> issued = PagePump.TryNavigateViaEvalAsync(
            (_, _) => throw new InvalidOperationException("window not ready"),
            new Uri("http://127.0.0.1:37933/"),
            30,
            CancellationToken.None);

        Assert.False(await issued);
    }

    /// <summary>应用退出取消照常上抛（R2 B1），不吞成 false。</summary>
    [Fact]
    public async Task CancelledToken_ThrowsAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await PagePump.TryNavigateViaEvalAsync(
            (_, _) => new ValueTask<string>("true"),
            new Uri("http://127.0.0.1:37933/"),
            30,
            cts.Token));
    }

    /// <summary>在途 eval 中取消同样上抛：覆盖 <c>when (ct.IsCancellationRequested)</c> 过滤分支。</summary>
    [Fact]
    public async Task CancelDuringEval_ThrowsAsync()
    {
        using var cts = new CancellationTokenSource();
        Task<bool> issued = PagePump.TryNavigateViaEvalAsync(
            async (_, token) =>
            {
                await Task.Delay(5000, token);
                return "true";
            },
            new Uri("http://127.0.0.1:37933/"),
            60,
            cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await issued);
    }
}
