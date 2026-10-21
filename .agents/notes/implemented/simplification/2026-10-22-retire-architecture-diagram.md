# Agent Note: 架构图退役（README 缩略图、交互版与图源全部删除）

Status: implemented

Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

## Problem

用户 2026-10-22 拍板：本仓不再提供架构图，README 的架构图与相关文件全部清理。

现状是三件套随仓：`docs/architecture.diagram.json`（唯一手写源，5600 B）、`docs/architecture.html`（`deliver` 产物，642704 B）、`assets/architecture.svg`（从 HTML 抽出的缩略图，57530 B）；`README.md`/`README.en.md` 的「工作原理」节各挂一行缩略图链接，`docs/architecture.md` 概览节嵌预览 + 交互版链接 + 图源规约。图无自动重生成入口：改图要人工「改 JSON → validate → deliver → 重抽 SVG → 双语 README 核对」。

漂移已经发生而无法就地修：术语批（`process/2026-10-22-assembly-surface-terminology`）退役「组合根」时未动图，图源内三处旧词以 `\uXXXX` 转义存在（按字形 grep 数不到），另有源内两处 schema 缺陷（缺 `meta.output`；`sys` 组件落在「桌面进程」boundary 框内却不在其 `wraps`），archify 2.17.0 重跑即报 `meta.output must be a portable POSIX-relative path` 与 `layout/boundary-encloses-non-member`；本机无 archify，债不可在本机清偿。

## Decision

删除图三件套，图从仓内消失：

- `git rm docs/architecture.diagram.json docs/architecture.html assets/architecture.svg`。
- `README.md`/`README.en.md` 的「工作原理」节删去缩略图与交互版两行；正文原有的「启动模型与分层见 docs/architecture.md（单一事实源）」保留，架构陈述的读物入口不变。
- `docs/architecture.md` 概览节删去交互版说明、静态预览与图源规约三段，概览只留文字条目。
- 两篇图 ADR（`architecture/2026-09-23-architecture-diagram-docs`、`architecture/2026-09-29-architecture-diagram-regen`）在 Status 下补 `Erratum`，随后移入 `archived/architecture/` 冻结（`Archived: 2026-10-22`）：它们描述的是已删除的产物，正文与当时的决定不改。
- 正文里把图当现存活面引用、或仍把「图挂账」写成代价的六篇笔记（`process/2026-10-22-assembly-surface-terminology`、`process/2026-10-22-assembly-surface-inventory-closure`、`process/2026-09-28-doc-adr-layer-cleanup`、`architecture/2026-09-29-dsh-follow-next-channel`、`architecture/2026-10-08-desktop-adapter-rename-final-assembly`、`simplification/2026-10-20-brand-icon-asset-removal`）各补一行 `Erratum`，指向本篇；正文不动。
- `HANDOFF-todos.md` 的「架构图债消化」`[ ]` 项随本批收尾撤除（本地工作档，不入 git）。

架构的单一事实源仍是 `docs/architecture.md` 正文 + `docs/architecture-standards.md`；可视化不再有仓内载体，需要时另行提案。

## Alternatives considered

- **修债后保留**（补 `meta.output`、把 `sys` 挪进 `wraps` 或移出 boundary、重生成三件套）：需要能跑 archify 的环境，且修完仍是「人工五步重生成」的漂移源——10-22 术语批已经证明文档改动会越过图。落败：保留成本是每次架构面改动多一道工序，收益是新人与评审的一次性看图。
- **只删 HTML 与 SVG，留 JSON 作轻量视图源**：JSON 在无渲染器时无人读，且仍是漂移源；留它会诱导下一个人「JSON 在，图就能再生成」。落败。
- **只删交互版，留 README 缩略图**：缩略图 57530 B 仍随仓，且 `docs/architecture.md` 概览嵌入的正是它；静态 SVG 无法就地更新、重生成同样依赖 archify。落败。
- **搬到 wiki 或外部托管**：需要第二处事实源与权限面，违反「每个事实只有一个家」（根 AGENTS.md 文档纪律）。落败。
- **以「人需要一眼看越层」为由保留缩略图**（即 `archived/architecture/2026-09-23-architecture-diagram-docs` 判「不画」落败的同一理由）：该理由只支持「有图比没图强」，不支持「必须随仓维护一张每次架构改动都要人工重生成的图」；2026-10-22 用户已明确不再提供图，越层关系由 `docs/architecture.md` 正文与 `architecture-standards.md` R1/R3 承载。落败。

## Consequences

- README 少一张视觉速览：新人靠「工作原理」正文与目录树定位，想深读走 `docs/architecture.md`；这是本条决定的直接代价，已接受。
- 仓内少 705834 B（HTML 642704 B + SVG 57530 B + JSON 5600 B，约 689 KiB）；archify 不再是本仓工具链依赖，`docs/architecture.md` 的图源规约段与 README 的双语镜像行同步消失。
- 机器门禁面无新增：`verify-md-links` 因引用同批清除保持绿，`verify-doc-budgets` 只减不增，无脚本把这三个路径当输入（打包脚本只复制 `assets/icons`、`icon.png`、`icon.ico`）。
- 架构图重生成挂账（`process/2026-09-28-doc-adr-layer-cleanup` 的挂账②）随图退役关闭，不再有「等 archify 环境」的待办。
- 两篇图 ADR 归档冻结；若未来重新引入可视化，按新 ADR 提案，不得复活归档篇。
- 残留：`verify-adr-format.py` 对 `archived/` 整目录跳过（存量门禁空洞，本批未扩），归档头正确性靠人工与 `verify-md-links` 兜底。

## Related

- [architecture-diagram-docs](../../archived/architecture/2026-09-23-architecture-diagram-docs.md)：建图决定（已归档）。
- [architecture-diagram-regen](../../archived/architecture/2026-09-29-architecture-diagram-regen.md)：最后一次重生成（已归档）。
- [assembly-surface-terminology](../process/2026-10-22-assembly-surface-terminology.md)：术语批，其图挂账由本篇关闭。
- [docs/architecture.md](../../../../docs/architecture.md)：架构陈述现存的唯一读物入口。
