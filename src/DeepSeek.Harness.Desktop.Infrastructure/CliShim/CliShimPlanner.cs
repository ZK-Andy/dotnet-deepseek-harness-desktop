namespace DeepSeek.Harness.Desktop.Infrastructure.CliShim;

/// <summary>
/// CLI shim 注册的纯规划结果：历史自产 pnpm shim 残留清理清单与 PATH 暴露（Unix 为 rc key 块）。
/// <paramref name="RuntimeNodeBinDir"/> 为系统全局 node 的 global bin 目录（无系统 node 时由桌面装到
/// 系统全局；把它一并暴露进终端 PATH，让桌面与终端共用同一份 node/dsh）。
/// </summary>
internal sealed record CliShimSetup(
    IReadOnlyList<string> LegacyShimPaths,
    string? ShellRcBlock,
    string? RuntimeNodeBinDir = null);

/// <summary>
/// CLI shim 注册纯逻辑（可单测）。pnpm 转发 shim 的生成已随 ADR companion-pnpm-prerequisite 撤除
/// （转发脚本不是正常形态，pnpm 由装配前置经 npm 装真身）；本规划仅保留两件事：
/// ①扫描 binDir 下的历史自产 pnpm shim 残留（升级用户机器上可能存在，PATH 顺序在前时会遮蔽
/// 引导新装的真 pnpm）；②系统全局 node bin 的终端 PATH 暴露（binDir 不再进 PATH——shim 已撤，
/// ~/.local/bin 由用户自管）。平台 IO（注册表 / rc 落盘 / 删除残留）与编排见 <see cref="CliShimRegistrar"/>。
/// </summary>
internal static class CliShimPlanner
{
    /// <summary>历史自产 pnpm shim 的文件名域（Unix 一名 + Windows cmd/ps1 两名，跨平台全扫）。</summary>
    public static readonly string[] LegacyPnpmShimNames = ["pnpm", "pnpm.cmd", "pnpm.ps1"];

    /// <summary>按平台构造规划：binDir 下的残留扫描清单；PATH 块只含系统全局 node bin
    /// （Windows 经注册表合并无 rc 块；Unix 无 node bin 可暴露时无 rc 块）。</summary>
    public static CliShimSetup BuildSetup(string binDir, bool isWindows, string? runtimeNodeBinDir)
    {
        var legacyShims = LegacyPnpmShimNames
            .Select(name => Path.Combine(binDir, name))
            .ToList();
        string? rc = !isWindows && runtimeNodeBinDir is not null
            ? CliShimPath.BuildShellExportBlocks(new[] { runtimeNodeBinDir }, ":")
            : null;
        return new CliShimSetup(legacyShims, rc, runtimeNodeBinDir);
    }
}
