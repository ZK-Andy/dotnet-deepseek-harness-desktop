# Agent Note: architecture-diagram-docs（全量架构图进 docs，README 只留缩略图链接）

Status: implemented

Review: FULL/2026-09-23/R1=ok R2=ok R3=ok

中文（双语暂不启用）

## Problem

用户拍板：画一张全量架构图放 `docs/architecture.md` 概览节，README 只加一行缩略图链接（SVG 预览 + 新页打开交互版），目录树不动。约束：README 双语镜像必须同步；图是视图不是契约（契约仍在代码 + `architecture-standards.md`）；产物可再生，不手改。

## Decision

用 archify `architecture` 类型、`showcase` 质量档生成 10 节点全量图（主链路用户→Ryn 壳→DesktopBootstrap→Core→运行适配→全局 dsh→共享 home，侧支更新平台/系统平台/伴生插件；两 region 边界：桌面进程、共享运行时）：

- `docs/architecture.diagram.json`：唯一手写源规约（archify validate 9/9、0 error 0 warning 后冻结）。
- `docs/architecture.html`：`deliver` 产物（640153 字节；visual-check 四档 containment 全过；correction_rounds 1）。
- `assets/architecture.svg`：HTML 内联 SVG 抽出 + 最小 CSS 内联（CDATA）+ `xmlns`/`data-theme="light"` 补齐 + 无值属性补 `=""`，xmllint 良构、Chrome 独立渲染验证通过。
- `docs/architecture.md` 概览节嵌入预览 + 交互版链接，注明"改图只改 JSON 并重新生成"。
- `README.md`/`README.en.md` 各加一行缩略图链接，ASCII 工作原理图与目录树保持不动。

## Alternatives considered

- **替换 README 目录树**：目录树是物理位置索引，新人靠它定位代码；架构图讲依赖方向，两者信息面不同，替换即丢信息。落败。
- **README 直接嵌全量交互 HTML**：GitHub README 不渲染 HTML 交互体，只能挂静态缩略图 + 外链；全量细节属 `docs/architecture.md` 粒度，放 README 臃肿且双源漂移。落败。
- **扩充 ASCII 图代替**：ASCII 无主题/缩放/搜索/追踪/导出，且复杂连线在等宽文本里不可读。落败（ASCII 两框运行时定位保留，职责不变）。
- **不画（codegraph 已够）**：codegraph 服务 AI 写代码的精确符号需求；架构评审/拍板/交接需要人一眼看越层，盒子图不可替代。落败。

## Consequences

- 图漂移时以代码 + `architecture-standards.md` 为准；改图流程：改 JSON → validate → deliver → 重抽 SVG → 同步双语 README（README.en.md 不可漏）。
- 新增三产物（JSON 5KB / HTML 640KB / SVG 54KB）随仓；visual-check 四 PNG 证据不进仓（看过即删）。
- 本批零代码、零行为变更：`dotnet` 链路不受影响；文档门禁（adr-format/md-links/doc-budgets）须全绿。

## Related

- [docs/architecture.md](../../../../docs/architecture.md)：概览节承载本图。
- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：规范（本图仅为其视图，非契约）。
- [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)：三项目解组织（本图结构来源）。
