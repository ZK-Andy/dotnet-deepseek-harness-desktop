namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// dsh 网页会话鉴权自愈裁决（纯函数可单测，ADR webauth-token-reentry）：token 第二跳提交后，
/// token→303→cookie 链可能在 WebView 未落定（mac-arm64 实证：6 到达含 token 跳，终页仍是 401 文本）。
/// 可见文本命中鉴权标记则有界重进 token URL；token 复用见下（复 present，非猜新）。
/// </summary>
public static class WebAuthRecovery
{
    /// <summary>
    /// 鉴权页标记：dsh 无会话时的 401 纯文本（上游 <c>dsh-client-connection</c> 固定英文串，
    /// 非 UI 词典文案——耦合点刻意小：正常 DSH UI 不含该串，误伤面仅限恰好渲染同串的 dsh 页面）。
    /// token 可复用证据：host 会话 curl 同一 token 两次 303【探索性，n=2，未留 artifact】。
    /// 辨析（R3 B1）：此处是复 present 同一 token URL（401 页自带"重开 URL"指引即此语义），
    /// 不是猜新 token——per-process launch token 不可猜，重进永不构造新 token。
    /// </summary>
    public const string AuthRequiredMarker = "authentication required";

    /// <summary>同一 URL 上的重进上限：扶一把（1 次），不赌博（无限重进在 cookie 根本缺失时变导航循环）。</summary>
    public const int MaxReentries = 1;

    /// <summary>鉴权自愈处置。</summary>
    public enum Disposition
    {
        /// <summary>页面正常（或探针未知）：放行，不动作。</summary>
        Healthy,

        /// <summary>鉴权页且重进预算未用完：重进 token URL。</summary>
        ReenterToken,

        /// <summary>鉴权页但预算耗尽：fail loud，不再重进。</summary>
        GiveUp,
    }

    /// <summary>
    /// 判定当前可见文本的处置。
    /// </summary>
    /// <param name="visibleText">页面可见文本采样（null = 探针超时/失败，按未知放行，绝不因观测失败触发重进）。</param>
    /// <param name="reentriesDone">已重进次数。</param>
    /// <returns>放行 / 重进 / 放弃。</returns>
    public static Disposition Evaluate(string? visibleText, int reentriesDone) =>
        visibleText is not null && visibleText.Contains(AuthRequiredMarker, StringComparison.Ordinal)
            ? (reentriesDone < MaxReentries ? Disposition.ReenterToken : Disposition.GiveUp)
            : Disposition.Healthy;
}
