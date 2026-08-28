# Agent Note: ci-docs-gate-independent-job

Status: implemented

## Problem

`ci.yml` 把「文档门禁」（`verify-adr-format` / `verify-cookbook` / `verify-doc-budgets` / `verify-md-links` / `verify-handoff-structure` / `verify-governance`）放在 `build-test` job 内，而 `build-test` 只在 `changes` job 判定的 `code` 面（`src/` `tests/` `scripts/` `*.slnx` `ci.yml`）命中时运行。由此产生门禁盲区：**改动落在 `plugins/`（如 companion JS 插件）、`docs/` 或仅改 ADR 时，连 `verify-*` 文档门禁都不跑**——CI run 只跑 `changes` 判定 `code=false`，`build-test` 跳过，整个 run 虽绿却几乎零验证价值（本地验证全靠人工/开发机，CI 层零兜底）。

## Decision

把「文档门禁」从 `build-test` 中拆出为**独立的 `docs` job**，**无条件触发**（不依赖 `changes.code`）；`build-test` 缩减为纯 dotnet 面（build + test with coverage + coverage summary + upload），仍只在 `code` 面命中时跑。

- `docs` job：checkout + 6 个 `verify-*` 脚本。对任何 push/PR 都跑——ADR、文档、插件（`plugins/`）、脚本、C# 改动一律过文档门禁。
- `build-test` job：仅 dotnet build/test/coverage。只在 `changes.code=='true'` 时跑（`src/tests/scripts/*.slnx/ci.yml`），避免纯文档/插件改动白跑 dotnet。

分工：`docs` gate 覆盖所有改动面的文档/ADR 合规；`build-test` 覆盖真实代码面（C#）。两者职责分离，互不拖累。

## Alternatives considered

- **把 `plugins/**` 加进 `changes.code` filter**（最小改动）：落败——plugins/ 改动会触发 `build-test` 跑 dotnet build/test，而 plugins/ 是 JS 插件、无 C# 变更，白跑 dotnet（浪费 CI 分钟），且语义混淆「code 面」= C# 代码而非"需验证的改动面"。
- **保持文档门禁在 build-test 内、仅靠本地验证**：落败——正是要修的盲区（plugins/docs 改动 CI 零验证），与"文档门禁是所有改动面的合规兜底"的目标相悖。
- **为 plugins/** 单独加一个 job**：落败——过度设计，plugins/ 也是文档门禁的覆盖对象，无需单独 job；统一由 `docs` job 覆盖更简。

## Consequences

- `plugins/`、`docs/`、ADR 改动现在都触发 `docs` job（verify-* 全跑），CI 层文档合规有兜底；不再依赖人工本地验证。
- `build-test` 只在真实 C# 代码面跑，纯文档/插件改动不白跑 dotnet（CI 分钟节省）。
- ci.yml 由 2 job（changes/build-test）增为 3 job（changes/docs/build-test）；`changes` 仍负责 code 判定、`docs` 无条件、`build-test` 条件。可维护性略增，职责更清晰。
- 行为边界：`docs` job 会跑 `verify-handoff-structure`（HANDOFF 存在即校验，clean CI 缺失时跳过），`verify-md-links`（排除 `.agents/notes/archived/`）——与 build-test 内原文档门禁行为一致。

## Related

- [workflow-and-gate-optimization-round](2026-08-27-workflow-and-gate-optimization-round.md)：`verify-handoff-structure` 等门禁接入 ci 的前一轮，本次把文档门禁从 build-test 独立为无条件 job。
