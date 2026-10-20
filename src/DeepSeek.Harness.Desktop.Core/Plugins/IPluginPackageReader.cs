namespace DeepSeek.Harness.Desktop.Core.Plugins;

/// <summary>
/// 插件包元数据读取端口（ADR core-purity-boundaries）：Core 只做判定与解析，**读盘在 Infrastructure**
/// ——Core 不碰文件系统（此前 PluginVersionCheck/ProfilePackageCheck 自读盘，Core 纯度只在程序集层成立）。
/// </summary>
public interface IPluginPackageReader
{
    /// <summary>读随包插件的 <c>package.json</c> 文本（<c>.tgz</c> 或目录两种形态）。</summary>
    /// <param name="spec">随包来源（tgz 路径或目录路径）。</param>
    /// <returns>package.json 文本；读不出返回 null（tgz 结构异常由实现抛——那是自己的产物，坏了必须可见）。</returns>
    string? ReadBundledPackageJson(string spec);

    /// <summary>读 profile 内已安装插件副本的 <c>package.json</c> 文本。</summary>
    /// <param name="profileDir">profile 目录。</param>
    /// <param name="packageName">包名（<c>node_modules/&lt;pkg&gt;/package.json</c>）。</param>
    /// <returns>package.json 文本；未安装或读不出返回 null。</returns>
    string? ReadInstalledPackageJson(string profileDir, string packageName);

    /// <summary>读 profile 的 <c>package.json</c> 文本。</summary>
    /// <param name="profilePkgPath">profile package.json 绝对路径。</param>
    /// <returns>package.json 文本；文件缺失或读不出返回 null。</returns>
    string? ReadProfilePackageJson(string profilePkgPath);
}
