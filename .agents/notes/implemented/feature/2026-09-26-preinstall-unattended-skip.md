# Agent Note: preinstall-unattended-skip（无人值守跳过）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

## Problem

mac CI 实证：`呈现可选插件 dshmarket，等待用户决策` 05:51:22 → 5 分钟超时默认跳过 → 05:58:26 dsh 才就绪。每次 CI 冒烟固定烧 5 分钟等人，而 CI 里永远没人点。真人首启可点选、不受影响，只有无人值守场景交税。

## Decision

- opt-in 环境变量 `DSH_DESKTOP_PREINSTALL_AUTO=skip`（大小写不敏感；未设/未知值一律走用户决策，fail-closed）：`RunPreinstallPhaseAsync` 入口直接跳过（日志留痕，可从应用内市场补装），UI 不呈现、不等待。
- 判定抽纯函数 `ShouldAutoSkipPreinstall()`（可单测）；三冒烟脚本 + rpm 容器全部设置该变量（CI 从此不等 5 分钟）。
- 默认关闭：真人首启行为零变化。

## Alternatives considered

- **冒烟改短超时（`PreinstallChoiceTimeoutMinutes` 覆写）**：落败——appsettings 是产品配置，CI 不应为省时间改产品语义；显式跳过意图更清晰。
- **CI 里自动选"安装"**：落败——装 dshmarket 拉长链路且无断言价值；跳过最快最稳。
- **去掉 dshmarket 首启呈现**：落败——那是产品面的对齐参照（ensure 可选插件），动它要产品拍板；本改只加逃生舱。

## Consequences

- CI mac 冒烟耗时 -5 分钟（npm + dsh 启动约 3 分钟为主体）；Windows 同理（npm 本身慢，另计）。
- 真人首启零变化；变量名进冒烟脚本注释（单一事实源在实现）。

## Testing

- `FirstBootPreinstallSkipTests` opt-in 矩阵（skip/SKIP/空/true/1）。
- 三脚本 `--self-test` 不受影响（环境变量只在真跑时注入）；真验证在 CI 冒烟日志（跳过行 + ①提前约 5 分钟）。
