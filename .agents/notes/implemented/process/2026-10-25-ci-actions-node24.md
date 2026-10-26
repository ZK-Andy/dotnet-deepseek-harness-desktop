# Agent Note: CI 官方 actions 升 node24 原生大版本

Status: implemented

Review: FULL/2026-10-25#3/R1=ok R2=ok R3=ok

## Problem

GitHub hosted runner 将 Node 运行时从 20 迁往 24，对声明 `runs.using: node20` 的 action 强制以 node24 执行并输出弃用警告；workflow 日志里每次跑都刷 `actions/cache@v4, actions/checkout@v4, actions/setup-dotnet@v4, actions/upload-artifact@v4 …` 一串。逐仓实测（action.yml `using:` 字段，run `40113917704` 日志为触发面）：本仓钉的 `checkout@v4/setup-dotnet@v4/setup-node@v4/cache@v4/upload-artifact@v4/download-artifact@v4/softprops/action-gh-release@v2` 全部 node20，是纯官方 action 声明滞后，与本仓脚本/应用无关；但每次跑都出警告、且未来 node20 兼容层移除时会硬失败。

## Decision

workflow 与 composite action 的 action 钉版按「最小 node24 原生大版本」升级：`checkout@v5`、`setup-dotnet@v5`、`setup-node@v5`、`cache@v5`、`upload-artifact@v6`、`download-artifact@v7`、`action-gh-release@v3`。同批并入 rpm 容器 `libatomic` 修复（见 [package-smoke-red-legs](../bug-fix/2026-10-25-package-smoke-red-legs.md) 改写）。

## Alternatives considered

- **维持现钉、等官方 v4 tag 原地更新**：落败——官方只在结点前临时强制转发，弃用警告每个 run 都刷；node20 兼容层移除时点是外部事件，被动等待把失败时机交给上游。
- **download-artifact 维持 v6**：落败——v6 为「preliminary node24 支持、默认仍 node20」的过渡版，v7.0.0（2025-12-12 发布）起默认 node24，变更面仅运行时 + punycode 修复、无 artifact 兼容性破坏（release notes 实证）；首版评审曾以「上游无 node24 版」主张维持 v6，该主张经 gh api 复核为探查面不全（止于 v6）的不实记录，已当场纠正改钉 v7。
- **只升 checkout（最长维护记录）**：落败——目标是一次清掉官方 action 警告面；零散升级让 workflow 内两代 node20/24 混跑，排查面更碎。

## Consequences

- `upload-artifact v4→v6`：v6 与 v4 的输入面逐项全等、v6.0.0 release notes 实证变更面仅 node24 运行时 + punycode 修复（评审轮 gh api 实证，非推断）；本仓 10 处上传全是「唯一名 + if-no-files-found」简单形态，无 merge/隐藏文件/overwrite 语义依赖。
- `action-gh-release v2→v3`：v3 = node24 运行时迁移，release.yml 传参 8 项在 v3 输入清单全在（评审轮实证）；该步仅 tag 触发，下次发版前无实跑面，发布是用户显式门（既有纪律），风险被该门隔离。
- **setup-node v5 记账**：`packageManager` 字段存在时会自动启用依赖缓存（[上游 PR #1348](https://github.com/actions/setup-node/pull/1348)）——本仓现态惰性（无根级 package.json、无该字段），但 companion 包将来加该字段会静默改变 CI 行为。node20 弃用背景见 [官方 changelog](https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners/)。
- **runner 版本前提记账**：download-artifact v7 与 upload-artifact v6 均要求 Actions Runner ≥ 2.327.1（各自 release notes IMPORTANT 块，[v7](https://github.com/actions/download-artifact/releases/tag/v7.0.0)/[v6](https://github.com/actions/upload-artifact/releases/tag/v6.0.0)）——本仓 release/package 全 GitHub-hosted runner 现态惰性，自托管 runner 引入时须复核。
- **第三方项收尾**：[`dorny/paths-filter` 升 v4](2026-10-26-paths-filter-v4-node24.md)（node24 原生；其补丁与安全修复不涉本仓消费面）。
- 机器定档：workflow 触 behavior-surface FULL（本篇随批三审）。

## Testing

- actionlint（1.7.12 钉版）+ verify-governance + verify-shell-standards 全绿；workflow 变更的实跑面 = run `40352264564`（`34d0d8d`）全作业绿。
