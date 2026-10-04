# Agent Note: 插件事务 staging 拷贝重建符号链接本体——悬空链不再卡死升级管线

Status: implemented

## Problem

用户实机（v0.5.18）companion 版本感知升级判定正常触发（host.log：`随包插件升级：dsh-desktop-companion 0.0.20 → 0.0.21`）但每次启动都失败：`Could not find file '~/.dsh/profiles/dotnet-desktop/node_modules/.bin/node-which'`。根因链：profile 的 `.bin/node-which` 是悬空符号链接（指向的 `which` 包已被卸除、链接残留）→ `PluginProfileTransaction.Begin` 的 staging 拷贝用 `File.Copy` **穿链**复制 → 悬空链打开 ENOENT 抛 `FileNotFoundException` → 整个拷贝中止、事务作废 → 升级永远失败。危害不止 companion：**任何 stale 链接的 profile 会让整条插件事务管线（升级 + 市场安装）永久卡死**，且每次启动重复失败（best-effort 吞掉不阻断启动，缺陷静默）。

另带出一处 .NET 行为陷阱：**.NET 7+ 的 `File.Exists` 对悬空符号链接返回 true**（lstat 语义）——「悬空」判定不能用存在性探测，必须解析链接目标本体。

## Decision

`PluginProfileTransaction.CopyDirectory` 对符号链接**重建链接本体**（读 `FileSystemInfo.LinkTarget` 原样重建，相对链接保持相对；目录链用 `Directory.CreateSymbolicLink`、文件/悬空链用 `File.CreateSymbolicLink`），绝不穿链复制内容——与仓库既有「拒链不穿链」纪律（`PathLinkGuard`/ADR profile-lock-path-symlink-rejection）对齐：数据路径的操作者不跟踪链接。悬空链同型重建并留一行日志（staged `dsh plugin add` 会重整 node_modules，重建的悬空链无存活面，结构保真即可）。`EnumerateFileSystemEntries` 单遍遍历替代原来 files/dirs 两遍。

## Alternatives considered

- **跳过悬空链不复制（落败）**：拷贝能过，但 staging 的 node_modules 结构与 active 失真（.bin 缺条目）；staged 探针 `dsh web:` 若依赖任一被跳过的链即假红。重建保真是零假设的语义。
- **只修用户机器上的残留链接、不改代码（落败）**：单机自愈，但所有「包已删链还在」的 profile（pnpm/npm 异常中断的普遍形态）都会卡死在同一条管线上——缺陷在拷贝实现，不在那一台机器。
- **拷贝前对 active 做悬空链清扫（落败）**：active 是用户数据目录，静默 unlink 有毁掉用户有意指向的风险（PathLinkGuard remarks 的既定立场）；staging 重建不碰 active，风险面为零。

## Consequences

- 买到的：事务管线对任意符号链接形态（pnpm 布局、.bin、悬空残留）的结构保真拷贝；升级/市场安装不再被 stale 链接卡死；`RecreateLink` 把悬空判定收敛到「解析目标本体」单点，规避 `File.Exists` 的 .NET 7+ 陷阱。
- 付出的：重建链接依赖 `File.CreateSymbolicLink`/`Directory.CreateSymbolicLink`（.NET 6+，三平台可用；Windows 需开发者模式/特权——CI runner 具备，RunMarkerTests 已有先例）；悬空目录链按文件链重建的类型偏差被 staged 重整吸收（注释已钉）。
- 实机自愈路径：升级本体的版本发布后，下次启动 staging 拷贝不再失败 → pnpm 重整 → 残留悬空链被收敛；staging 侧结构保真使「跳过即假红」不成立。

## Related

- [transactional-plugin-pipeline](../architecture/2026-09-13-transactional-plugin-pipeline.md)——staging 拷贝/换入管线本体；本篇修其拷贝步的链接语义。
- [profile-lock-path-symlink-rejection](2026-09-16-profile-lock-path-symlink-rejection.md)——「拒链不穿链」纪律的来源；本篇把该纪律延伸到拷贝操作。
