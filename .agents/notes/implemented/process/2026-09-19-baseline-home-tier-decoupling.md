# Agent Note: 基线值机器家移出 docs（跟值批保持 LIGHT 档）

Status: implemented

Review: FULL/2026-09-19#3/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

基线值有两个落点参与机器判定：README 双语徽章与一份机器可解析的家。只要这份机器家位于 `docs/**`，两条既有规则就叠加出与风险无关的成本——`docs/**` 在 [review-tier-escape-proofing](2026-09-03-review-tier-escape-proofing.md) 里属行为契约面、整批判 FULL，而跟值批（只改测试数与覆盖率两个数字）必然改到它：每次跟值都命中 `behavior-surface: docs/testing.md`，必须携本批新产的 `Review: FULL/<date>/R1=ok R2=ok R3=ok` 证据才能过 pre-commit / pre-push / CI 三闸（实测分类与复现见 Testing）。跟值台账强制每次追加一行，既定做法因而连带刷新该台账 ADR 的 `Review:` 行——最近三批真跑了 R1/R2/R3 三路。

跟值不改契约：它把 CI cobertura 实测值抄进基线，口径、判定面、来源规则都不动。`docs/**` 一刀切是刻意的（2026-09-03 档位逃逸后按「宁可拦错不漏」），逐文件或逐内容开豁免会把执行者判据重新引回机械定档。两条路取其一：把基线值移出命中的路径面。

## Decision

基线值的机器家 = `scripts/test-baseline.json`，顶层**恰好**两键：`tests`（`<passed>/<total>`）与 `coverage`（`<rate>%`）。来源事实（CI 作业 id、并集 covered/valid、代码面）仍只住 [coverage-baseline-from-ci-cobertura](../testing/2026-09-12-coverage-baseline-from-ci-cobertura.md) 的跟值台账，不复制进机器家。

- [README 徽章门禁](../testing/2026-09-13-readme-badge-baseline-gate.md) 的解析源改为该文件：缺文件、非法 JSON、非对象、缺键/多键、值形状不符一律 fail loud；README 双语徽章仍须与之逐字相等；`--staged` 仍判 index。
- `docs/testing.md` 只留口径与指针，不写当前数值；门禁表指向机器家。
- `scripts/verify-review-tier.py` 的 `docs/**` 触发器不动；`--self-test` 增一例夹具，钉住「跟值批（README 双语 + `scripts/test-baseline.json` + 台账 ADR）判 LIGHT」。
- 跟值动作不变：收尾检查单「README 双语同步」条触发，人把 CI cobertura 值同时写进机器家与两枚徽章。

## Alternatives considered

- **给 `docs/testing.md` 开内容级豁免（只放行基线行数值变更）**：落败——判据要读 diff 内容并与「只改这一行」的窄条件对齐，正是机械定档刻意排除的执行者判据面；同批顺手改口径段时豁免即失效，跟值批转档变得不可预期。
- **新增 BASELINE 档（跟值批要 R2 复算数字）**：落败——档位体系从两档变三档，`verify-review-tier.py` 的判定与简报门禁的 lane 派生同步复杂化；跟值批的风险面是「抄错数字」，而数字与 CI 打印值的一致性已由 `coverage-summary.py` 复算与徽章相等门禁两头兜，不构成独立档位。
- **删掉机器家，只留 README 徽章一个家**：落败——徽章值是手写静态 URL 载荷，没有对照物时抄错/漏改无机械拦截；`verify-readme-badges.py` 与 [2026-09-13 的漂移教训](../testing/2026-09-13-readme-badge-baseline-gate.md) 一并作废，等于把两个家的漂移换成单点静默错值。
- **机器家放根目录或 `.agents/`**：落败——`scripts/` 已有同类机器消费清单（`scripts/doc-budgets.manifest.json` 由 `verify-doc-budgets.py` 读），放同处命名与邻里一致；根目录堆文件、`.agents/` 是协作机制层，机器数据不混入。
- **维持现状（每次跟值走三审）**：落败——成本全落在两个数字上，而所有 FULL 批都可用刷新 `Review:` 行满足门禁更说明该档保护的是「有无跑评审」而非「值对不对」；跟值的正确性证据是数字对账，不是契约面评审。

## Consequences

- 跟值批的变更面 = `README.md` + `README.en.md` + `scripts/test-baseline.json` + 台账 ADR，全部落在 FULL 触发器之外 → 判 LIGHT，按 [review-scope-narrowing](2026-08-31-review-scope-narrowing.md) 的触发枚举与零行为变更判据定轻审或简化路径。
- `docs/**` 的契约面保护不变：口径段、门禁表、复现步骤仍在 `docs/testing.md`，其改动照旧 FULL。
- 代价：`docs/testing.md` 不再直显当前数值，读者要多一跳（README 徽章或机器家）。机器家成为形状契约，改键名或值形状须同步 `scripts/verify-readme-badges.py`。
- 本批零 `src/tests` 变更；基线内容未动（CI `35389108336`，695/695、57.69%）。

## Testing

- `python3 scripts/verify-readme-badges.py --self-test`：14 例夹具 / 16 条断言（一致通过 / coverage 不符 / tests 不符按 README 报 / 徽章缺失 / 机器家非 JSON / 多键 / 值形状不符 / 非对象 / 重复键 / 工作树一致而 index 漂移 / 机器家缺失 / README 缺失 / 同种徽章重复 / index 内 blob 不可解码 / 机器家位置在 `docs/**` 外）。
- `python3 scripts/verify-review-tier.py --self-test`：25 例夹具全绿，夹具 25 钉住「跟值批（README 双语 + `scripts/test-baseline.json` + 台账 ADR）判 LIGHT」。
- 真实仓库复验：`verify-readme-badges.py` 工作树与 `--staged` 均 exit 0；`verify-review-tier.py --staged`（base = HEAD，即本批暂存集）判 FULL 并列出 4 个触发点（`.githooks/pre-commit`、`docs/testing.md`、两个 `verify-*.py`），随变更携带本批新产证据行后 `--staged --enforce` exit 0；跟值批路径集复算 `_classify` 为 `(False, [])`。

## Related

- [README 事实徽章与基线一致性机械校验](../testing/2026-09-13-readme-badge-baseline-gate.md)：本批改换解析源的门禁。
- [覆盖率基线取 CI cobertura 实测](../testing/2026-09-12-coverage-baseline-from-ci-cobertura.md)：值的来源与人工跟值义务。
- [评审档判定机械强制](2026-09-03-review-tier-escape-proofing.md)：`docs/**` 判 FULL 的规则家；本批不动其触发器。
- [评审范围收窄](2026-08-31-review-scope-narrowing.md)：LIGHT 档的触发枚举与零行为变更判据。
