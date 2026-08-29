namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 随包插件清单（ADR bundled-plugin-version-aware-catalog + bundled-plugin-registry-normalization；
/// online-first 调整见 ADR online-first-unbundled-runtime）：
/// 每个随包/安装器自带插件 tgz 的安装与升级主体。启动装配对清单逐项执行统一判定——未装即装、
/// 本地形态已装则来源版本更新即升（离线路径）、registry 形态已装则放手（与自装等价，绝不回拉）。
/// 清单是唯一扩展点，新增随包插件只在此登记，装配逻辑零改动。
/// 「是否随包」逐案拍板决定成员，「是否版本感知」凡随包即适用，「是否归化 registry」逐案拍板，三层解耦。
/// </summary>
public static class BundledPluginCatalog
{
    /// <summary>清单项：<paramref name="ResolveSpec"/> 返回 <see langword="null"/> 表示无任何来源（如开发用 PATH dsh），调用方跳过；
    /// 解析器抛出的异常按单插件跳过处理，不影响其余清单项。
    /// 参数依次为运行时目录（捆绑/引导下载同布局）与安装器自带插件资源目录（resources/plugins，可为 null）。
    /// <paramref name="NormalizeToRegistry"/> = 已装的本地形态（file:/link:）安装是否归化为 registry 自管
    /// （ADR bundled-plugin-registry-normalization）：仅对有 registry 上游的第三方插件开启
    /// （dshmarket 开、companion 关——companion 无 registry，安装器自带是唯一分发面）。</summary>
    public sealed record Entry(string Package, Func<string, string?, string?> ResolveSpec, bool NormalizeToRegistry = false);

    /// <summary>清单顺序即单条 <c>plugin add</c> 的 spec 顺序。</summary>
    public static readonly IReadOnlyList<Entry> All =
    [
        new("dshmarket", (runtimeDir, _) => MarketInstallHelper.ResolveMarketSpec(runtimeDir), NormalizeToRegistry: true),
        new("dsh-desktop-companion", MarketInstallHelper.ResolveCompanionSpec),
    ];

    /// <summary>
    /// 组装本次启动需要安装/升级/归化的插件待装清单（纯逻辑，可单测）。
    /// 判定复用 <see cref="PluginVersionCheck"/> 三件套：未装即装；已装按
    /// profile dependencies 的 spec 形态分流——registry 形态直接放手（与自装等价，绝不回拉，
    /// ADR bundled-plugin-registry-normalization）；本地形态（file:/link:）且 spec 为本地路径时
    /// 比对来源 tgz 与已装副本版本，来源更新即入列（离线路径），同版或更高且 <paramref name="catalog"/> 项
    /// 开启归化时改为入列 registry 归化条目（spec = 裸包名 → latest）；脏版本串 fail loud
    /// 记日志跳过该插件（升级与归化一并放弃）；spec 缺失（null）或解析器抛错按单插件跳过。
    /// spec 为 registry 串（online-first 无闭包形态的 <c>dshmarket@latest</c> 回退）时跳过版本
    /// 比对——registry 串读不出本地版本，直接归化本地形态存量（版本跟随上游，@latest 即最新）。
    /// <see cref="MarketInstallHelper.IsPathSpec"/> 为 false 的条目（归化/registry 回退串）
    /// 标记 <paramref name="pending"/> 第三元 <c>FromRegistry</c> = true，供消费点把
    /// registry 触碰条目与离线可靠的本地路径条目分组成 spawn 事务。
    /// </summary>
    /// <param name="catalog">随包插件清单。</param>
    /// <param name="runtimeDir">运行时目录（捆绑或引导下载，解析各插件 spec）。</param>
    /// <param name="installerPluginsDir">安装器自带插件资源目录（resources/plugins）；开发/引导形态可 null。</param>
    /// <param name="profilePkg">profile 的 package.json 绝对路径。</param>
    /// <param name="profileDir">profile 目录（读取已装副本版本）。</param>
    /// <param name="log">诊断日志出口（host.log 同款行文）。</param>
    /// <returns>待安装/升级/归化的 (包名, spec, 是否 registry 触碰) 列表，顺序与清单一致。</returns>
    public static List<(string Package, string Spec, bool FromRegistry)> AssemblePending(
        IEnumerable<Entry> catalog,
        string runtimeDir,
        string? installerPluginsDir,
        string profilePkg,
        string profileDir,
        Action<string> log)
    {
        var pending = new List<(string Package, string Spec, bool FromRegistry)>();
        foreach (var entry in catalog)
        {
            string? spec;
            try
            {
                spec = entry.ResolveSpec(runtimeDir, installerPluginsDir);
            }
            catch (Exception ex)
            {
                // 吞掉的只是单个插件的 spec 解析异常：清单扩展点下未来解析器实现出错时，
                // 其余插件的装配与安装不受牵连（隔离粒度与版本比对段一致）。
                log($"[host] 随包插件 {entry.Package} spec 解析失败，跳过：{ex.Message}");
                continue;
            }

            if (spec is null)
            {
                log($"[host] 随包插件 {entry.Package}：无可用来源（安装器资源/运行时目录均未携带），跳过");
                continue;
            }

            if (!MarketInstallHelper.IsBundleInstalled(profilePkg, entry.Package))
            {
                log($"[host] 随包插件 {entry.Package} 未就位，加入待装清单");
                pending.Add((entry.Package, spec, !MarketInstallHelper.IsPathSpec(spec)));
                continue;
            }

            if (entry.NormalizeToRegistry)
            {
                var installedSpec = MarketInstallHelper.ReadDependencySpec(profilePkg, entry.Package);
                if (!MarketInstallHelper.IsLocalSpec(installedSpec))
                {
                    // registry（或别名/github 等非本地）形态 = 用户侧/registry 所有：放手不碰，
                    // 即便来源钉版更高也不回拉 file:——回拉会让市场的更新检查与自更新再次失效。
                    // dependencies 值不可读（null）同样保守不碰：凭未知数据断言所有权有回拉风险。
                    log(installedSpec is null
                        ? $"[host] 随包插件 {entry.Package} 已装 spec 形态不可读，保守跳过（不回拉）"
                        : $"[host] 随包插件 {entry.Package} 已为 registry 安装（spec={installedSpec}），交由市场自管，跳过");
                    continue;
                }
            }

            if (!MarketInstallHelper.IsPathSpec(spec))
            {
                // registry 回退串（如 dshmarket@latest）：无本地产物可比对版本——本地形态存量
                // （上方已放行的唯一剩余形态）直接归化，版本交由 @latest 解析。离线失败留痕后
                // 下次启动重试；成功即 registry 形态，本判定幂等。
                if (!entry.NormalizeToRegistry)
                {
                    log($"[host] 随包插件 {entry.Package} 为 registry spec 且未开启归化，跳过");
                    continue;
                }

                log($"[host] 随包插件归化：{entry.Package}（本地形态存量，registry 回退 spec={spec}）→ registry 自管");
                pending.Add((entry.Package, $"{entry.Package}@latest", FromRegistry: true));
                continue;
            }

            try
            {
                var bundledVersion = PluginVersionCheck.ReadBundledVersion(spec);
                var installedVersion = PluginVersionCheck.ReadInstalledVersion(profileDir, entry.Package);
                if (PluginVersionCheck.NeedsUpgrade(installedVersion, bundledVersion))
                {
                    log($"[host] 随包插件升级：{entry.Package} {installedVersion ?? "(不可读)"} → {bundledVersion}");
                    pending.Add((entry.Package, spec, FromRegistry: false));
                    continue;
                }

                if (entry.NormalizeToRegistry)
                {
                    // 本地形态且来源不比已装更新：归化为 registry 自管。spec 必须带显式 @latest——
                    // pnpm 对「已存在的依赖」裸名 add 按既有 spec 幂等安装（file: 永不翻转，
                    // v0.3.12 实机实证），显式 @latest 才会重新解析 registry 并改写 spec。
                    // 失败（离线/registry 错误）留痕后下次启动重试；成功即 registry 形态，本判定幂等。
                    log($"[host] 随包插件归化：{entry.Package}（本地形态随包安装）→ registry 自管");
                    pending.Add((entry.Package, $"{entry.Package}@latest", FromRegistry: true));
                }
            }
            catch (Exception ex)
            {
                // 随包产物损坏或版本串非法必须可见（fail loud）；只跳过本插件的升级/归化检查，
                // 不拖垮其余插件与启动流程。
                log($"[host] {entry.Package} 版本比对失败，跳过升级与归化检查：{ex.Message}");
            }
        }

        return pending;
    }
}
