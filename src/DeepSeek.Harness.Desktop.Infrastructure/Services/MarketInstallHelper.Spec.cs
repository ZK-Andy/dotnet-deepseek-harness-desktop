using System.Text.RegularExpressions;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>市场 registry 插件 spec 的形状 allowlist（ADR spawn-env-and-plugin-spec-hardening）：
/// 以上游 Electron 桌面 <c>packageNameFromSpec</c>（<c>apps/desktop/src/project-manager.ts:162</c>）
/// 判据为基，另拒 <c>link:</c>、<c>file:</c>/<c>link:</c> 前缀按大小写不敏感匹配。
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> 只免 shell 注入，前导 <c>-</c>
/// 仍会被下游当 flag，故在拼参数前收口。</summary>
public static partial class MarketInstallHelper
{
    /// <summary>插件 spec 来源：registry（市场/预设）套 allowlist；随包 <c>file:</c> 是内部可信路径
    /// （Windows 安装目录可含空格），只做调用方自持的形状。</summary>
    internal enum PluginSpecOrigin
    {
        /// <summary>registry 安装面：套 <see cref="IsValidRegistrySpec"/>。</summary>
        Registry,

        /// <summary>随包本地 tgz 路径：不套 registry allowlist。</summary>
        Bundled,
    }

    /// <summary>npm 包名形状（对齐上游 <c>PACKAGE_NAME_PATTERN</c>：小写字母数字开头，scoped 需 <c>/</c>）。</summary>
    [GeneratedRegex("^(?:@[a-z0-9][a-z0-9._~-]*/[a-z0-9][a-z0-9._~-]*|[a-z0-9][a-z0-9._~-]*)$")]
    private static partial Regex PackageNamePattern();

    /// <summary>版本或 dist-tag 形状（对齐上游 <c>VERSION_PATTERN</c>：允许 <c>latest</c>/<c>alpha</c> 等 tag）。</summary>
    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+_-]*$")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// 校验一个 registry spec（<c>name</c> / <c>name@version|tag</c> / <c>@scope/name[@version|tag]</c>）。
    /// 拒绝：空串、前导 <c>-</c>、含空白或反斜杠、含 <c>://</c>、<c>file:</c>/<c>link:</c> 前缀、
    /// scoped 缺 <c>/</c>、包名或版本形状不合。
    /// </summary>
    /// <param name="spec">待校验 spec。</param>
    /// <param name="reason">不合法原因（合法时为空串）。</param>
    /// <returns>合法返回 true。</returns>
    internal static bool IsValidRegistrySpec(string spec, out string reason)
    {
        if (string.IsNullOrEmpty(spec))
        {
            reason = "空 spec";
            return false;
        }

        if (spec.StartsWith('-'))
        {
            reason = "前导 '-' 会被下游解析为 flag";
            return false;
        }

        if (spec.Any(char.IsWhiteSpace) || spec.Contains('\\'))
        {
            reason = "含空白或反斜杠";
            return false;
        }

        if (spec.Contains("://", StringComparison.Ordinal))
        {
            reason = "含 URL 方案";
            return false;
        }

        if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
        {
            reason = "本地路径 spec 不属于 registry 安装面";
            return false;
        }

        if (spec.StartsWith('@') && spec.IndexOf('/') < 0)
        {
            reason = "scoped spec 缺少 '/'";
            return false;
        }

        (string name, string? version) = SplitSpec(spec);
        if (!PackageNamePattern().IsMatch(name))
        {
            reason = $"包名形状不合法：{name}";
            return false;
        }

        if (version is not null && !VersionPattern().IsMatch(version))
        {
            reason = $"版本或 tag 形状不合法：{version}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>按 npm spec 语义切分包名与版本/tag（scoped 的版本分隔符取 scope 之后的首个 <c>@</c>）。</summary>
    private static (string Name, string? Version) SplitSpec(string spec)
    {
        int at = spec.StartsWith('@') ? spec.IndexOf('@', spec.IndexOf('/') + 1) : spec.IndexOf('@');
        return at < 0 ? (spec, null) : (spec[..at], spec[(at + 1)..]);
    }
}
