# Agent Note: cookbook 冷归档层承载机制已移除的条目

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

三审结论：R3 报 1 Blocker——归档层页头自称受 `verify-cookbook.py` 契约约束，而该脚本缺省只校验主档，归档层实际无门禁覆盖（「自称有契约、无人校验」）。已同批收口：脚本缺省改为校验 cookbook 家庭（主档 + 归档层，存在即校验；显式路径优先），自测加三项家庭选择夹具，并反向验证（往归档层植入违约条目 → 门禁 exit=1），故归档层搭既有 pre-commit/CI 调用点即被覆盖。R1/R2 0 Blocker。采纳——指针由打包节末改置页头且去掉逐条枚举（枚举只留归档页头一处）、`AGENTS.md` 字数预算表加行、`docs/testing.md` 两行口径同批修正；拒绝——直接删条目（判别细节要留）与迁到未提交的 `.plan/`（指针会成悬空）。

## Problem

`docs/cookbook.md` 预算贴顶（上限 2700，迁出前实测 2697），其中一批条目的机制已从仓库移除：online-first 共享 home 改造后，闭包打包/per-arch 瘦身/源码图裁剪的机器（`scripts/bundle-runtime*.sh`、`trim_runtime_closure`）都已从仓库删除，而条目自述「机器已随 online-first 退役、仅存量 tag 包有意义」。它们继续占预算，把仍在服役的判别挤在贴顶线上；待办里记的触发式腾挪（「下次需为新条目腾预算 ≥10 词时迁出」）让下一批新增条目先卡在预算上。此前一批的处置是就地压缩这些尾巴——那只是把问题往后推。

## Decision

- 新增**冻结冷归档层** `docs/cookbook-archive.md`：只收「机制已从仓库移除」的条目，页头写明分层语义（只减不增；回迁须原子改两处；条目沿用 cookbook 的阶段标签与格式契约）。
- 迁出四条（内容逐字搬，只把「已随 online-first 退役」式叙述交给页头统一承载）：闭包形态与 per-arch 瘦身边界、打包踩坑全链（0.1.3→0.1.9）、`Linux 打包要点` 的闭包段、DevTools `.map` 404 裁剪判别。
- 仍在服役的条目留在 cookbook，只剪掉退役尾巴：`[打包] Linux 打包要点`（闭包段与尾注移走，rpm/deb 依赖、自检判据、staging 校验、CI 预览/SHA256SUMS、本地不产包全留）；`[打包] GitHub Actions 缓存作用域隔离`（去掉「先例键已退役」——该机制仍在服役，`package-windows.yml` 仍用 `actions/cache` 缓存 npm 下载缓存）。
- cookbook 的页头（而非某一节）留一行指针指向归档层：迁出条目来自多个阶段节，分层关系是页级的。
- **归档层接入机器门禁**：`scripts/verify-cookbook.py` 缺省改为校验「cookbook 家庭」——主档 + 归档层（存在即校验，缺归档层的克隆仍通过；显式传路径时只校验该档），自测加家庭选择夹具。这样归档层自动搭上既有的 pre-commit 与 CI 调用点，无需新增调用行——归档层自称受该格式契约约束，就必须真被它校验。
- 预算登记：`scripts/doc-budgets.manifest.json` 为归档层登记 300 词上限（迁出时 272），根 `AGENTS.md` 字数预算表同步加行，cookbook 上限维持 2700（迁出后 2479）。

## Alternatives considered

- **直接删条目（历史留 git）**：落败——它们承载存量 tag 包的判别细节（`.map` 404 的判据、rpm `brp-strip` 误伤、`find -type f` 解压形态），删掉后要重建得翻 git 历史；冷归档层的成本只是一个文件加一行指针。
- **迁到 `.plan/`（未提交的本地工作文档层）**：落败——cookbook 是提交面文档，指针必须指向提交面可解析的目标；指到 gitignore 的文件本地能过 `verify-md-links`、新克隆即成悬空引用。
- **迁进 owning ADR 的 Consequences**：落败——ADR 是决策记录、格式由 `verify-adr-format` 机器强制，把打包判别塞进决策记录会混淆两个层。
- **直接给 cookbook 提额**：落败——doc-budgets 的既定顺序是先迁层/精简、才允许提额；且这批内容本就不描述当前状态。

## Consequences

cookbook 从 2697 词降到 2479（腾出 218 词），下一批新增条目不再先卡预算线。代价：多一个提交面文档与其预算条目；查退役机制要多点一次指针（指针就在打包节末，语义自明）。

## Testing

- `python3 scripts/verify-cookbook.py`：缺省跑两档（主档 49 条 + 归档 3 条）OK；`--self-test` 10 夹具全过（含新增的家庭选择三项：归档缺席只查主档 / 归档在场两档并查 / 显式路径优先）。反向验证：往归档层植入一条违约条目，门禁即 exit=1。
- `python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json`（cookbook 2479/2700、归档 272/300）、`python3 scripts/verify-md-links.py`（OK）。
- 迁出内容逐字比对：只有「已随…退役」式叙述被页头吸收，判别细节无删减。
