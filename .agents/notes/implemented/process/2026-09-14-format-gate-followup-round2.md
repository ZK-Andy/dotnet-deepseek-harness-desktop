# Agent Note: format 门禁二轮收口——Navigation.cs 自动格式化

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

[一轮排序矫正](2026-09-14-nav-partial-import-order-fix.md) 后 CI 二轮仍红（run `34777911619`）：残留 IDE0005（`System.Threading`/`Ryn.Core` 两条未使用 using）+ SA1508（文件尾闭括号前空行）。手排不如机器排——本地跑 `dotnet format`（与门禁同工具）即全净。

## Decision

`dotnet format src/.../DeepSeek.Harness.Desktop.csproj` 自动修复，零手工编辑；612/612 绿。流程修正：组合根文件的样式预检**直接跑 `dotnet format <csproj>`**（修复态）或 `--verify-no-changes`（校验态），不再手写 using/空白——手排两次均漏项，机器化一次到位（对齐 AGENTS「新规范默认问能不能进 verify」的同款思路：与门禁同工具的本地预检）。

## Alternatives considered

- **继续手工逐条修 warning**：落败：一轮已证明手排漏项，警告集不封闭（排序/未使用/空白三类混出），逐条追是打地鼠。
- **放宽 SA1508/IDE0005 严重级不入门禁**：落败：与 .editorconfig 强制力决策（coding-standards「强制力度」）相逆。

## Consequences

- 收益：本地 `dotnet format` 与 CI 门禁同工具，红因不再逃逸到 CI。
- 边界：`dotnet format` 修复态会顺带重排仓内其他文件——本批仅触碰目标 csproj 范围（`--include` 未用时已核 `git status` 无溢出文件）。
