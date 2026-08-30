namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 可选（preset）插件清单（ADR reference-alignment 批次二）：registry 安装、首启引导页可勾选。
/// companion（internal）不在此列——companion 是桌面壳必需品，保持 spawn 前静默自愈（对齐参照
/// <c>ensure_internal_plugins</c>，不出现在引导勾选清单）。
/// 当前仅一个 preset（dshmarket 市场）；安装路径为市场特化（<see cref="MarketInstallHelper.EnsureMarketFromRegistryAsync"/>，
/// 其 <see cref="MarketInstallHelper.MarketSpec"/> 即 dshmarket@latest）。新增可选插件时需在此登记
/// 名称并扩展对应安装路径——本清单的「呈现（PendingForFirstBoot）」与「实际安装」当前都收敛到 market 一族。
/// </summary>
public static class PresetPluginCatalog
{
    /// <summary>首启引导的可选插件：插件市场（registry 安装）。</summary>
    public const string Market = "dshmarket";

    /// <summary>preset 插件名清单（渲染与呈现顺序）。</summary>
    public static readonly IReadOnlyList<string> All = [Market];

    /// <summary>
    /// 返回仍需首启引导呈现的可选插件（未就位者）——已就位的跳过，重复引导/升级场景不重复打扰。
    /// 纯逻辑可单测；<paramref name="log"/> 为诊断出口（可选）。
    /// </summary>
    /// <param name="profilePkg">profile 的 package.json 绝对路径。</param>
    /// <param name="log">诊断日志出口（host.log 同款行文）。</param>
    /// <returns>待引导呈现的插件名列表，顺序与 <see cref="All"/> 一致。</returns>
    public static List<string> PendingForFirstBoot(string profilePkg, Action<string>? log = null)
    {
        var pending = new List<string>();
        foreach (string pkg in All)
        {
            if (!MarketInstallHelper.IsBundleInstalled(profilePkg, pkg))
            {
                log?.Invoke($"[host] 可选插件 {pkg} 未就位，列入引导清单");
                pending.Add(pkg);
            }
        }

        return pending;
    }
}
