namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 宿主横幅注入脚本的单一工厂：宿主主动注入的顶部横幅（版本底线 / 非受控退出 / 更新就绪）
/// 共用同一 DOM 结构与堆叠规则，消除多处手拼脚本的镜像漂移。
/// </summary>
/// <remarks>
/// 堆叠偏移在注入时按「已存在的已知横幅数量」运行时计算（每张 44px）——
/// 多横幅同现时依次下移，不再互相压叠；新增横幅只需登记 id 并声明 <see cref="BannerTone"/>，无需改动其他横幅的守卫链。
/// </remarks>
public static class DesktopBanner
{
    /// <summary>全部宿主横幅 id（堆叠计数依据；新增横幅必须登记在此）。</summary>
    public static readonly string[] KnownIds =
    {
        "dsh-desktop-version-floor-banner",
        "dsh-desktop-run-marker-banner",
        "dsh-desktop-update-ready-banner",
    };

    /// <summary>横幅语义色调：只决定确认按钮的语义填充色；表面（底/字/分隔线）恒中性，
    /// 随 dsh 主题自适应。横幅生于 dsh 页面之上、主题 CSS 必然在场。</summary>
    public enum BannerTone
    {
        /// <summary>警示（如版本底线）：琥珀色按钮（<c>state-warn-primary</c>）。</summary>
        Warn,

        /// <summary>中性确认（如非受控退出）：反色填充按钮（label-primary ↔ bg-overlay 镜像）。</summary>
        Neutral,

        /// <summary>成功（如更新就绪）：绿色按钮（<c>state-success-primary</c>）。</summary>
        Success,
    }

    /// <summary>生成横幅注入脚本（纯函数可单测）：幂等 id 守卫 + 运行时堆叠偏移。
    /// 配色单点映射：表面消费 <c>--dsw-alias-bg-overlay/label-primary/border-l2</c> 随主题，
    /// 按钮按 <paramref name="tone"/> 取语义 token；硬编码值仅为主题 CSS 不在场时的防御性回退。</summary>
    /// <param name="id">横幅 id（须在 <see cref="KnownIds"/> 登记语义，幂等守卫依据）。</param>
    /// <param name="text">横幅文案（经 JsString 管线转义注入）。</param>
    /// <param name="tone">语义色调（决定按钮填充色）。</param>
    /// <param name="okLabel">确认按钮文案（宿主按注入时刻 locale 选择，ADR host-ui-locale；
    /// 缺省中文「知道了」，单一事实源在 <see cref="UiCopy"/>）。</param>
    public static string Build(string id, string text, BannerTone tone, string? okLabel = null)
    {
        okLabel ??= UiCopy.OkLabel(english: false);
        string buttonBackground = tone switch
        {
            BannerTone.Warn => "var(--dsw-alias-state-warn-primary,#b45309)",
            BannerTone.Success => "var(--dsw-alias-state-success-primary,#16a34a)",
            _ => "var(--dsw-alias-label-primary,#0f1111)",
        };
        string buttonText = tone == BannerTone.Neutral
            ? "var(--dsw-alias-bg-overlay,#fff)"
            : "#fff";
        string known = string.Join(",", KnownIds.Select(k => "'" + k + "'"));
        return "(function(){" +
               "var id='" + id + "';" +
               "if(document.getElementById(id))return;" +
               "var known=[" + known + "];" +
               "var n=0;" +
               "for(var i=0;i<known.length;i++)if(document.getElementById(known[i]))n++;" +
               // 堆叠基准点 = 自绘顶栏下缘（Frameless 后顶栏常驻页面顶部，ADR frameless-uniform-caption-bar）；
               // 顶栏未注入（0）时行为与旧基准点一致
               "var cap=document.getElementById('" + CaptionBar.ElementId + "');" +
               "var base=cap?cap.getBoundingClientRect().height:0;" +
               "var b=document.createElement('div');" +
               "b.id=id;" +
               "b.style.cssText='position:fixed;top:'+(base+n*44)+'px;left:0;right:0;z-index:2147483647;display:flex;gap:12px;align-items:center;justify-content:center;padding:8px 16px 8px 40px;background:var(--dsw-alias-bg-overlay,#fff);color:var(--dsw-alias-label-primary,#0f1111);font:13px/1.5 system-ui,sans-serif;border-bottom:1px solid var(--dsw-alias-border-l2,rgba(0,0,0,.1))';" +
               "b.textContent=" + AppJsonContext.JsString(text) + ";" +
               "var x=document.createElement('button');" +
               "x.textContent=" + AppJsonContext.JsString(okLabel) + ";" +
               "x.style.cssText='flex:none;padding:2px 10px;background:" + buttonBackground + ";color:" + buttonText + ";border:0;border-radius:6px;cursor:pointer;font-size:12px';" +
               "x.onclick=function(){b.remove()};" +
               "b.appendChild(x);" +
               "(document.body||document.documentElement).appendChild(b);" +
               "})();";
    }

    /// <summary>dsh 版本底线横幅注入脚本（纯函数可单测）：告知探测版本、底线与后果。
    /// 判定核（探测/底线比较）在 Infrastructure <c>RuntimeVersionGate</c>；横幅属表示面，随本工厂收拢。</summary>
    /// <param name="detectedVersion">探测到的 dsh 版本串。</param>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）。</param>
    public static string BuildVersionFloorBanner(string detectedVersion, UiLocale? uiLocale = null)
    {
        bool english = uiLocale?.IsEnglish == true;
        return Build(
            "dsh-desktop-version-floor-banner",
            UiCopy.VersionFloorBannerText(detectedVersion, RuntimeVersionGate.MinimumVersion, english),
            BannerTone.Warn,
            uiLocale?.OkLabel);
    }

    /// <summary>非受控退出横幅注入脚本（纯函数可单测）：不暗示应用故障，引导导出诊断。
    /// 取证核（run-marker 落盘/清理）在 Infrastructure <c>RunMarker</c>；横幅属表示面，随本工厂收拢。</summary>
    /// <param name="uiLocale">UI 语言单点（可选，缺省中文，ADR host-ui-locale）。</param>
    public static string BuildUncleanExitBanner(UiLocale? uiLocale = null)
    {
        bool english = uiLocale?.IsEnglish == true;
        return Build(
            "dsh-desktop-run-marker-banner",
            UiCopy.UncleanExitBannerText(english),
            BannerTone.Neutral,
            uiLocale?.OkLabel);
    }
}
