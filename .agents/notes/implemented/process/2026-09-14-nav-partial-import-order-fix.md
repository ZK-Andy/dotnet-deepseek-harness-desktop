# Agent Note: DesktopBootstrap.Navigation.cs using 排序矫正

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`2ef0896`/`58b6e06` 拆出 `DesktopBootstrap.Navigation.cs` 时手写 using 顺序违反 `.editorconfig` 排序规则，CI `build-test` ubuntu 腿 format 门禁红（run `34777611456`）；本机 `dotnet build` 不含该检查故未拦。仅样式，包资产与行为零影响（v0.4.11 三平台 package 与 release 均绿）。

## Decision

using 按 `.editorconfig` 序重排（System* → Microsoft.Extensions → Ryn），无其他改动。流程侧：组合根文件任何微改同样触发 review-tier FULL 证据要求——属既定纪律（防逃逸），不设样式豁免；后续拆分组合根文件时应本地跑 `dotnet format --verify-no-changes` 预检后再提交。

## Alternatives considered

- **给 review-tier 增设「纯样式豁免」**：落败：豁免分类即逃逸面开端（discipline 的价值在无例外），且本仓已把 format 门禁机器化，人工判「纯样式」反而引入不可机械化的灰色地带。
- **回滚并重新拆分文件**：落败：与重排同代价，多一次历史噪音。

## Consequences

- 收益：CI format 门禁复绿基线恢复。
- 代价/边界：无行为变更（612/612 不变）；「本地 build 绿 ≠ format 绿」的踩坑记入流程认知——组合根改动预检面 = build + format 两项。
