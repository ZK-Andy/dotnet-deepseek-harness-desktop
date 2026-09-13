namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 随包插件装配判定（ADR bundled-plugin-version-aware-catalog + online-first-unbundled-runtime 批次三；
/// B1 迁 Core）：对供给清单逐项执行统一判定——未装即装、已装而来源版本更新即升、与来源同版或更高即跳过。
/// 具体供给清单（成员与来源解析器）在主工程组合侧 <c>BundledPluginSupply</c> 登记，装配逻辑零改动。
/// </summary>
public static class BundledPluginCatalog
{
    /// <summary>清单项：<paramref name="ResolveSpec"/> 返回 <see langword="null"/> 表示无任何来源（如开发用 PATH dsh），调用方跳过；
    /// 解析器抛出的异常按单插件跳过处理，不影响其余清单项。
    /// 参数为安装器自带插件资源目录（resources/plugins，可为 null）——online-first 后随包唯一供给源。</summary>
    public sealed record Entry(string Package, Func<string?, string?> ResolveSpec);

    /// <summary>
    /// 组装本次启动需要安装/升级的插件待装清单（纯逻辑，可单测）。
    /// companion 语义：未装即装（spec 为本地 tgz/目录）；已装则比对来源与已装副本版本，
    /// 来源更新即入列（升级），与来源同版或更高即跳过；spec 缺失（null）或解析器抛错按单插件跳过。
    /// </summary>
    /// <param name="catalog">随包插件清单。</param>
    /// <param name="installerPluginsDir">安装器自带插件资源目录（resources/plugins）；开发/引导形态可 null。</param>
    /// <param name="profilePkg">profile 的 package.json 绝对路径。</param>
    /// <param name="profileDir">profile 目录（读取已装副本版本）。</param>
    /// <param name="log">诊断日志出口（host.log 同款行文）。</param>
    /// <returns>待安装/升级的 (包名, spec) 列表，顺序与清单一致。</returns>
    public static List<(string Package, string Spec)> AssemblePending(
        IEnumerable<Entry> catalog,
        string? installerPluginsDir,
        string profilePkg,
        string profileDir,
        Action<string> log)
    {
        var pending = new List<(string Package, string Spec)>();
        foreach (Entry entry in catalog)
        {
            string? spec;
            try
            {
                spec = entry.ResolveSpec(installerPluginsDir);
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
                log($"[host] 随包插件 {entry.Package}：无可用来源（安装器资源未携带），跳过");
                continue;
            }

            if (!ProfilePackageCheck.IsBundleInstalled(profilePkg, entry.Package))
            {
                log($"[host] 随包插件 {entry.Package} 未就位，加入待装清单");
                pending.Add((entry.Package, spec));
                continue;
            }

            try
            {
                string bundledVersion = PluginVersionCheck.ReadBundledVersion(spec);
                string? installedVersion = PluginVersionCheck.ReadInstalledVersion(profileDir, entry.Package);
                if (PluginVersionCheck.NeedsUpgrade(installedVersion, bundledVersion))
                {
                    log($"[host] 随包插件升级：{entry.Package} {installedVersion ?? "(不可读)"} → {bundledVersion}");
                    pending.Add((entry.Package, spec));
                }
            }
            catch (Exception ex)
            {
                // 随包产物损坏或版本串非法必须可见（fail loud）；只跳过本插件的升级检查，
                // 不拖垮其余插件与启动流程。
                log($"[host] {entry.Package} 版本比对失败，跳过升级检查：{ex.Message}");
            }
        }

        return pending;
    }
}
