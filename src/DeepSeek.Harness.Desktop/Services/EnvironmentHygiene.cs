using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>子进程环境净化单点（ADR spawn-env-and-plugin-spec-hardening）：我方 spawn 的
/// node/npm/dsh 子进程只带自有注入，不继承宿主 shell 的 <c>NODE_OPTIONS</c> 与
/// npm/pnpm/corepack 配置环境（上游 Electron 宿主 spawn 处同款 denylist，
/// <c>apps/desktop/src/host-process.ts:108-111</c>）。</summary>
/// <remarks>与上游的两处刻意偏离：①上游连 <c>DSH_DESKTOP_*</c> 一并剥离，我方保留——该前缀承载
/// 本壳自有注入（孤儿清扫 token，ADR self-update-exit-reaps-dsh-child 缺口 B），剥了会失去
/// 跨启动复验能力；②<c>NODE_OPTIONS</c> 按大小写不敏感匹配（上游为精确匹配），更严且 fail-closed。
/// 调用约定：先 <see cref="StripInherited"/>，再写我方变量。</remarks>
internal static class EnvironmentHygiene
{
    /// <summary>该环境变量名是否属于宿主继承噪声：<c>NODE_OPTIONS</c> 与
    /// <c>npm_</c>/<c>pnpm_</c>/<c>corepack_</c> 前缀，均大小写不敏感（Windows 环境名不区分大小写；
    /// 上游对 <c>NODE_OPTIONS</c> 是精确匹配，本壳更严）。</summary>
    /// <param name="name">环境变量名。</param>
    /// <returns>属于噪声返回 true。</returns>
    internal static bool IsInheritedNoise(string name) =>
        name.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("npm_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("pnpm_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("corepack_", StringComparison.OrdinalIgnoreCase);

    /// <summary>从 <see cref="ProcessStartInfo.Environment"/> 剥离继承噪声（先快照后删，避免边遍历边改）。</summary>
    /// <param name="psi">已构造的进程启动信息。</param>
    internal static void StripInherited(ProcessStartInfo psi)
    {
        foreach (string name in psi.Environment.Keys.Where(IsInheritedNoise).ToList())
        {
            psi.Environment.Remove(name);
        }
    }
}
