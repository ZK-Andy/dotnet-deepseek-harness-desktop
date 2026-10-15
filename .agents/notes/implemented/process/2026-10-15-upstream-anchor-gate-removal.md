# Agent Note: 上游锚点闸拆除（跟随上游接受漂移）

Status: implemented

Review: FULL/2026-10-15/R1=ok R2=ok R3=ok

Related: 被撤销的前篇 [`2026-10-13-upstream-anchor-probe`](2026-10-13-upstream-anchor-probe.md)（已插 Erratum，正文留历史）。

## Problem

上游锚点探测闸（周检工作流 + 探测脚本 + 锚清单）为注入面漂移而立，但漂移失败模式静默、周检定时臂须等时钟验证。跟随官方 UI 即接受其改名节奏：定期检查的成本高于收益，漂移走用户报修再修；将来自定义界面摆脱上游依赖时再立新闸。

## Decision

拆除整闸：删独立工作流 `.github/workflows/upstream-anchors.yml`、探测脚本 `scripts/verify-upstream-anchors.py`、锚清单 `scripts/upstream-anchors.json`；`ci.yml` 去锚夹具自测行；`docs/testing.md` 与根 `AGENTS.md` 去闸引用（写当前状态，不留变更叙事）。前篇插 `Erratum` 注销决定、正文保留。通道语义（`@next` 正式 / `@alpha` 预览）是产品事实，不受影响，保留。

## Alternatives considered

- **只删定时臂、留脚本与手动触发（落败）**：探针本体虽可手动跑，但无人会定期手动跑，与全拆的实际效果相同，却留下无人维护的脚本与清单；不如拆净，将来重立。
- **保留周检等周一验证（落败）**：用户已判定定时检查本身无意义；为一次 cron 验证再等一个周末不值得。
- **drift 发生时再补选择器级断言（落败/暂缓）**：起真实页面断言成本远超 token 断言，且自定义界面之前无稳定断言面；走用户报修。

## Consequences

- 买到的：周检工作流、定时等待项与脚本维护成本清零。付出的：上游改名致样式静默缺失时，只能靠用户报修发现；锚清单（哪些注入依赖哪些上游 token）随脚本同删，将来重立须重新盘点 `used_by`。
- 自定义界面落地时，本篇即被取代（届时写新篇交叉链接）。

## Testing

- `verify-md-links` 全绿（无悬空引用）；`verify-adr-format` 全绿（含本篇与前篇 Erratum 行）。
- `verify-doc-budgets` 全绿（`AGENTS.md`/`testing.md` 只减字数）。
- `actionlint` 对改后的 `ci.yml` 绿；push 后 `ci` 全绿即实跑验证（独立工作流已删，无 dispatch 验证面）。
- `verify-review-tier --staged` 定档后走三重审核（工作流 + 门禁脚本删除，预期 FULL）。
