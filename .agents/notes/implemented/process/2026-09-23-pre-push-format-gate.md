# Agent Note: pre-push 补 format 门禁与 outgoing 增量判定

Status: implemented

Review: FULL/2026-09-23/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`a3de599` 提交时本地记「门禁全绿」，CI ubuntu 腿仍以 IDE0005 拦下（`dotnet format --verify-no-changes --severity warn`，`25875fe` 跟进修复）。根因两处：pre-push 无 format 校验（与 CI 门禁不同工具）；增量判定只看工作树/index（`git diff --name-only HEAD` + cached），已提交的 outgoing 变更整个跳过 `dotnet` 段——提交后工作树干净即全跳，CI 才暴露。

## Decision

- pre-push 增量面 = outgoing（`origin/main...HEAD`，缺失时回退工作树/index）+ 工作树 + 已暂存，三者去重；`src/**`、`tests/**`、`*.slnx`、`/.editorconfig` 命中任一即跑 `dotnet` 段（format 结论源全在触发器内）。
- `dotnet` 段内补与 CI 同命令的 format 校验（`dotnet format dotnet-deepseek-harness-desktop.slnx --verify-no-changes --severity warn`，与 build 同 env），置于 build 之后、test 之前——格式红即 fail loud，不再逃逸到 CI。
- 文档-only 推送仍跳过 `dotnet` 段（快检查纪律不变，CI 拥有穷尽矩阵）。

## Alternatives considered

- **hook 加 format 但增量判定不动**：落败——本次逃逸的主因是已提交变更被跳过，只加 format 仍拦不住「提交后干净推送」形态。
- **改增量判定但不加 format（只跑 build/test）**：落败——build 不报 IDE0005（主工程不开 `GenerateDocumentationFile`，见 [ide0005-enforce-via-format-gate](2026-08-31-ide0005-enforce-via-format-gate.md)），test 亦不覆盖，缺 format 即缺唯一强制路径。
- **pre-push 无条件全量跑 format**：落败——文档-only 推送被拖慢，违 hooks 快检查纪律；format 结论翻转源（src/tests 内容、`.editorconfig`、slnx 结构）已全在触发器内，其余纯文档改动不影响该命令结论。

## Consequences

- 收益：与 CI 同工具的本地预检前移到 push 前；`a3de599→25875fe` 类往返不再发生。
- 代价：含 src/tests 变更的推送多一次 `dotnet format --verify` 耗时（与 CI 同命令，本地已实测可接受）；`origin/main` 缺失时回退旧语义（提示行可见）。
- 验证：`bash -n` 语法、`verify-review-tier.py --staged/--since` 定档、`dotnet format --verify-no-changes` 本地 exit 0。

## Related

- [ide0005-enforce-via-format-gate](2026-08-31-ide0005-enforce-via-format-gate.md)（implemented）：IDE0005 唯一强制路径的 single source。
- [format-gate-followup-round2](2026-09-14-format-gate-followup-round2.md)（implemented）：与门禁同工具的本地预检先例。
