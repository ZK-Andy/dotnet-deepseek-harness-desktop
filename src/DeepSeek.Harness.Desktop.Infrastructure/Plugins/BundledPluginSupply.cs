namespace DeepSeek.Harness.Desktop.Infrastructure.Plugins;

/// <summary>
/// 随包插件供给清单（组合侧登记）：online-first 后随包仅桌面伴生插件（companion），
/// 安装器资源是唯一供给源；dshmarket 不由壳安装（需要时由用户在 dsh 侧自装），
/// 不作为随包/种子条目。清单顺序即单条 <c>plugin add</c> 的 spec 顺序；
/// 新增随包插件只在此登记，Core 侧装配判定（<see cref="BundledPluginCatalog.AssemblePending"/>）零改动；
/// 「是否随包」逐案拍板决定成员。
/// </summary>
public static class BundledPluginSupply
{
    /// <summary>清单项：<paramref name="resolveSpec"/> 的 <see langword="null"/> 返回值表示无任何来源
    /// （如开发用 PATH dsh），调用方跳过；解析器抛出的异常按单插件跳过处理，不影响其余清单项。</summary>
    public static readonly IReadOnlyList<BundledPluginCatalog.Entry> All =
    [
        new("dsh-desktop-companion", MarketInstallHelper.ResolveCompanionSpec),
    ];
}
