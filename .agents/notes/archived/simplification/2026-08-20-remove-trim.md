# Agent Note: remove-trim-shrink

Status: implemented
Archived: 2026-08-28

## Problem

`scripts/bundle-runtime-ci.sh` 里有一段从未在 CI 启用的 `TRIM=1` 体积裁剪占位：对整棵 `node_modules` 用 `find -delete` 删 `*.md`（除 `LICENSE*`）/ `*.ts` / `*.map` / `__tests__/`。它是低收益高风险的备胎——裁剪面非白名单、可能删掉某包惰性 require 的运行时文件埋下潜伏故障，且 CI 自检只 spawn `dsh web:` 抓不到；对 1.5G Windows 包（大头在 node 二进制 + 整树编译后 JS + 各架构 prebuild）收效甚微；本地 `bundle-runtime.sh` 又没有同逻辑，造成 CI/本地闭包不一致。

## Decision

**删除 `TRIM=1` 体积裁剪代码路径**：移除 `bundle-runtime-ci.sh` 的裁剪块，清理 `docs/development.md`（`# 裁剪：TRIM=1 ...` 行）与 `docs/architecture.md`（`TRIM=1 时裁剪 *.md/*.ts` 表述）中的引用，并同步 `HANDOFF.md` 待办（体积裁剪不再作为沿用 `TRIM` 的待办）。`bundle-runtime-ci.sh` 保持"整树 `node_modules` + `dsh web:` 自检"不变，CI 照常出未裁剪包。

## Alternatives considered

- **保留 TRIM 占位**：落败——从未启用、收益与风险不成比例，留着是被动接受的误导性功能面。
- **把 TRIM 收紧成白名单 + 完整功能回归再接入 CI**：落败（另议）——真要把体积降下来（尤其 1.5G Windows）需 per-arch 去重/删无关平台 prebuild 的精准方案，属独立专案；本次仅判断 `TRIM` 这个特定实现不值得保留，不预判未来裁剪方案形态。
- **把裁剪逻辑同步进本地 `bundle-runtime.sh`**：落败——本地和 CI 本就该产同一闭包，给一个高风险的裁剪同时在两处维护只会放大问题。

## Consequences

- 收益：去掉一个从未启用、可能埋潜伏运行时故障且误导性强的占位代码路径；`bundle-runtime-ci.sh` 更简单、行为更可预期（整树闭包原样打包）。
- 代价：少了一个"顺手减几 MB"的选项；体积问题仍存在，留待独立专案（per-arch 去重 / 白名单裁剪 + 裁后完整功能回归），已移出沿用 `TRIM` 的待办。
- 一致性：CI 与本地 `bundle-runtime.sh` 都产未裁剪整树闭包，无 CI/本地差异。

## Related

- [2026-08-20-trim-runtime-closure-per-arch](../../implemented/simplification/2026-08-20-trim-runtime-closure-per-arch.md)：本决定的「CI 产未裁剪闭包」现状已被其取代——CI 现执行白名单式 per-arch 裁剪 + strip-sourcemap 注释剥除；否决 `TRIM` 盲删的理由仍有效。
