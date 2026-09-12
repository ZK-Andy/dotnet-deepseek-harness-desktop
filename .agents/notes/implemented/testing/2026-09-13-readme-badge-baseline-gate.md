# Agent Note: README 事实徽章与基线行一致性机械校验

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`README.md` / `README.en.md` 的 tests 与 coverage 徽章，和 `docs/testing.md` 基线行描述同一事实（测试通过数、覆盖率），是两个家：徽章是发布面门脸，基线行是复现口径的锚。两个家的跟值义务只有一条人工动作——[session-close](../../../workflows/session-close.md) 第 4 条「README 双语同步」；`verify-*.py` 不跑 `dotnet test`，CI 也不校验徽章，徽章值由手写静态 URL（`img.shields.io/badge/*`）决定。

该缺口已两次产生实际漂移：徽章曾停在 `25/25` 而实测 300+、数月无人察觉（session-close 开头的教训记录）；2026-09-12 收尾又发现徽章停在 `55.37%` 而 CI cobertura 为 `55.22%`（[coverage-baseline-from-ci-cobertura](2026-09-12-coverage-baseline-from-ci-cobertura.md) 的跟值义务段已指向本门禁）。

## Decision

新增门禁脚本 `scripts/verify-readme-badges.py`（默认 fail loud，违约 exit 1）：

- 基线唯一解析源 = `docs/testing.md` 的基线行 `当前 <passed>/<total>，覆盖率 <rate>%`。该行不匹配即 FAIL——形状变化不得静默跳过。
- 对 `README.md` 与 `README.en.md` 各取 `img.shields.io/badge/tests-*` 与 `img.shields.io/badge/coverage-*` 徽章值，URL 解码后要求等于基线值（`<passed>/<total>`、`<rate>%`）；任一侧缺徽章即 FAIL。
- 校验面 = 这两项事实徽章；`release` / `downloads` / `stars` / `platform` / `.NET` 等由外部服务或静态常量派生，不入本门禁。
- `--staged` 读 index（`git show :<path>`，即将入库的树）而非工作树：`pre-commit` 步骤 11 用它，把基线漂移暂存、再把工作树改回一致不能顶包（同第 10 步 `verify-review-tier.py --staged` 的理由）。
- 接线：`.githooks/pre-commit` 步骤 11（`--staged`）、`ci.yml` 的 `docs` job「文档门禁」步（工作树 = 检出内容）、`AGENTS.md` 质量门清单、`docs/testing.md` 门禁表。

**边界**：门禁只断言两个家**相等**；值的来源与人工跟值义务在 [coverage-baseline-from-ci-cobertura](2026-09-12-coverage-baseline-from-ci-cobertura.md)。

## Alternatives considered

- **只在 `verify-doc-budgets.py` 里加断言**：落败——该脚本职责是字数预算且由 manifest 数据驱动，事实一致性塞进去会让失败信息的归属含混，两类门禁的语义不同。
- **CI 里真跑 `dotnet test` 取 cobertura 再比对徽章**：落败——把纯文档批次推回 dotnet 全量作业，用 CI 分钟与 runner 成本换一行文案一致性；本仓口径已定「基线取 CI cobertura、由人跟值」，门禁只需防两个家漂移。
- **徽章动态化（shields endpoint / Gist / Coveralls）**：落败——已在 coverage-baseline ADR 判落败（外部状态、凭据与可用性面），此处不重开。
- **只断言 coverage 徽章、不管 tests 徽章**：落败——两条徽章与基线行是同一行事实（`当前 569/569，覆盖率 55.22%`）、同一失败模式，只堵一侧等于让 tests 侧继续裸奔。
- **正则宽松匹配（匹配不到就跳过该项）**：落败——静默跳过正是「数月无人察觉」的成因，门禁必须在形状变化时 fail loud。

## Consequences

- 收益：徽章与基线行的漂移在 `pre-commit` 与 CI `docs` job 两处被拦，不再依赖收尾时的自觉核对。
- 代价：`docs/testing.md` 的基线行成为机器解析的形状契约——改该行的措辞或标点须同步 `scripts/verify-readme-badges.py` 的正则。
- 本批零 `src/tests` 变更；`dotnet test` 仍 569/569、0 警告。

## Testing

`python3 scripts/verify-readme-badges.py --self-test`：10 个夹具 / 11 条断言（一致通过 / coverage 不符 / tests 不符按 README 报 / 徽章缺失 / 基线行形状变化 / 工作树一致而 index 漂移 / 基线文档缺失 / README 缺失 / 同种徽章重复 / index 内 blob 不可解码）。真实仓库复验：工作树与 `--staged` 两种模式均 exit 0；非 UTF-8 的 `docs/testing.md` 在两种模式都报违规（`missing` / `not valid UTF-8`）而非回溯退出。

## Related

- [覆盖率基线取 CI cobertura 实测](2026-09-12-coverage-baseline-from-ci-cobertura.md)：徽章值的来源与人工跟值义务。
- [session-close](../../../workflows/session-close.md)：第 4 条「README 双语同步」是本门禁覆盖的人工作业面。
