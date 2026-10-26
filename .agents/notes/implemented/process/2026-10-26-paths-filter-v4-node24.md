# Agent Note: 第三方 paths-filter 升 v4（node24 原生）

Status: implemented

Review: FULL/2026-10-26/R1=ok R2=ok R3=ok

## Problem

`dorny/paths-filter` 的 `@v3` 声明 `using: node20`（其 `action.yml`；run `40352264564` 的 changes 作业 annotation 是当时唯一 node20 来源，该 run 跑在升前的 pin 上），runner 因此在其上每轮打弃用警告。上游 [`v4.0.0`](https://github.com/dorny/paths-filter/releases/tag/v4.0.0)（2026-03-12，唯一变更即 runtime 升 node24）起为 node24，[`v4.0.3`](https://github.com/dorny/paths-filter/releases/tag/v4.0.3)（2026-08-05）为当前最新（评审轮 `gh api` 实测）。node20 强制转发的机制与影响见 [CI actions 升 node24](2026-10-25-ci-actions-node24.md)。

## Decision

`dorny/paths-filter@v3` → `@v4`，浮动大版本（与其余 action 的钉法一致）。输入名与默认值在 v3/v4 不变；本仓只消费 `filters:` 输入与 `outputs.code`（布尔），v4.0.3 的补丁面（`some-with-excludes` 新枚举、`list-files` shell/csv 多行文件名转义的安全修复 [GHSA-7hc6-8hq5-9q2m]）都在该消费面之外。

## Alternatives considered

- **维持 v3**：落败——警告每轮刷，且 node20 兼容层移除后该作业硬失败；修复成本就是这一行 pin。
- **钉 `@v4.0.3` 精确版本**：落败——与其余 action 的浮动大版本钉法不一致，多出第二种升级路径。
- **弃用该 action，自行用 `git diff --name-only` 算过滤**：落败——把过滤逻辑搬进 `run:` 块，扩大受 shellcheck/governance 守卫的面，而 `changes` 作业正是为免重复实现才用它。

## Consequences

- 浮动大版本会把补丁直接带进过滤判据：v4.0.1 merge-queue 支持、v4.0.2 `rev-parse`/dubious-ownership 修复（[compare v4.0.0…v4.0.3](https://github.com/dorny/paths-filter/compare/v4.0.0...v4.0.3)），v4.0.3 的 `some-with-excludes` 与上述安全修复。失效面落在 `ci.yml` 的 `needs.changes.outputs.code`：补丁若把变更判成 false，整条 build-test 静默不跑。
- 现态 `v4` tag 与 v4.0.3 指向同一提交（评审轮 `gh api` 实测），无未消费的更新版本。

## Testing

- actionlint 与 `verify-governance.py` 全绿。
- run `40357813146`（`7429f75`）全作业绿：日志中 `Node.js 20` 行数 0（升前 run `40352264564` 为 1），build-test 三腿与 docs/changes 均绿。
