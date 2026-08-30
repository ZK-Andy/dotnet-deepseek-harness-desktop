# Agent Note: architecture-standards（确立架构规范）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

项目迭代速度极高（14 天 337 提交、近 7 天 196 提交），代码出现「屎山」征兆：**组合根膨胀**（`DesktopBootstrap` 873 行 + `DesktopBootstrap.Startup` 537 行）、**大文件**（`RuntimeBootstrap` 743 / `HarnessRuntimeHost` 534 / `MarketInstallHelper` 448 / `CliShimRegistrar` 363）、**债务只累积无人清账**（4 处代码 `TODO(...)` 简化候选 + StyleCop 元素排序约 310 处存量违规）、**风格规范严而结构/债务无约束**。诊断：`docs/coding-standards.md` 已完成「一段代码怎么写对」（格式/命名/惯用，机器强制），但**没有任何架构规范**约束「系统怎么被组织」（层/依赖方向/边界/组合根/行为契约）；`docs/architecture.md` 是**描述性**（现状），非**规范性**（应如何）——不做约束不回答「新功能放哪层、组合根该不该有业务、组件会不会过大」。单项目 + 文件夹若不控依赖方向会退化为意大利面（Microsoft 官方架构指引明确点名）。

## Decision

**确立 `docs/architecture-standards.md` 为架构规范（单一事实源）；采纳权威原理、不照抄 4 项目模板、按单进程桌面壳落地。本批只落规范 + ADR，不做代码拆分。** 行为契约（async/取消/异常/日志/IPC）经后续 `architecture-mechanization` 通道〇迁回 `coding-standards.md` 归属（见该 ADR），架构规范只留结构+契约。

- **基准来源**：Microsoft [.NET Application Architecture guides](https://dotnet.microsoft.com/en-us/learn/dotnet/architecture-guides) + [Architect modern web applications with ASP.NET Core and Azure](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/)（官方权威）；[Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)/[Onion](https://jeffreypalermo.com/blog/the-onion-architecture-part-1/)/[Hexagonal (Ports-and-Adapters)](https://blog.ploeh.dk/2013/12/03/layers-onions-ports-adapters-its-all-the-same/)（同一原理不同命名）。
- **采纳原理、不照抄模板**：本项目是**单进程桌面壳**（Ryn + dsh 运行时），**不采用** Clean Architecture「Domain/Application/Infrastructure/Presentation 分项目」模板（对该规模属过度设计）；**采纳**其底层原理——分离关注点、依赖方向朝内、依赖倒置（DIP）/端口-适配、组合根、可测性——按本应用形状落地。
- **规范内容**（`docs/architecture-standards.md`）：五条规则 R1 组合根纪律 / R2 依赖方向 / R3 外部边界抽象（Ports/Adapters，含 IPC 帧契约）/ R4 反上帝对象尺寸健康闸（健康提示，非编码规范）/ R5 与 coding-standards 分工；并给出逻辑分层表（组合根 / 应用域逻辑 / 子域 / 外部边界）。行为契约（async/取消/异常/日志）归 `coding-standards.md`（经 architecture-mechanization 通道〇迁出）。
- **行为契约归属（后经 architecture-mechanization 通道〇定案）**：async/取消、异常策略、日志属「单段代码怎么写」，迁回 `coding-standards.md`（预算提额 500→1000）；`architecture-standards.md` 只留结构+契约，不再含行为契约。
- **R4 定位为「健康检查/完成定义」，非编码规范**：规避此前「方法 ≤30 行当规范」被否的教训（权威规范无行数规则）；以「组件 ~400 行 / 方法 ~80 行 / 组合根含业务 → 评审/门禁提示」作开发完成即检的健康闸。
- **本批范围**：只落 `docs/architecture-standards.md` + 本 ADR + AGENTS 入口/预算 + doc-budgets manifest；**不做代码拆分**（`DesktopBootstrap` 等按规范重构是下一步实现模式的事）。

## Alternatives considered

- **行为契约并入 `coding-standards.md`（方案 B）**：async/取消/异常/IPC 属编码规范；但会**爆 500 词预算**（budget 规则要求提额需 PR 说明理由），且把「行为契约（跨组件）」与「风格（单代码）」混在一处。落败；采方案 A——行为契约归架构规范。
- **照抄 Clean Architecture 4 项目模板（Domain/Application/Infrastructure/Presentation）**：技术栈等同、结构最清晰；但对单进程桌面壳（~8.8k 行、Ryn/interop/IPC 重度绑定）属**过度设计**——多项目 + 依赖注入容器引入远超收益的复杂度，且桌面壳非 web/微服务，无「切换基础设施实现」的实际动机。落败；采纳原理不照抄模板。
- **只写 `architecture.md` 描述、不建规范**：描述性文档无约束力，不回答「新功能放哪层/组合根该不该有业务/组件会不会过大」；官方指引明确单项目+文件夹不控依赖方向 → 意大利面。落败；需规范性文档。
- **只落文档不落 ADR**：非平凡治理变更（建立规范/规则）须 ADR 记录取舍；仅文档会引入「边界与理由」缺失导致重议。落败。
- **本批同时拆 `DesktopBootstrap` 等**：规范刚立即对既有上帝对象动刀，风险与范围都过大；规范先定型、重构单独走实现模式（feature-flow + 三重审核）。落败（延后，非取消）。

## Consequences

- 架构从「无约束」变「有规范」：R1 组合根纪律直接抑制 873 行上帝对象继续生长；R2/R3 约束依赖方向与边界抽象（Ryn/dsh 进程/companion IPC/文件/网络/更新 feed），治「散落 + 意大利面」；R4 反上帝对象提供健康闸；R5 界定与 coding-standards 的分工（行为契约归后者）。三篇 `TODO(...)` 简化候选、StyleCop 元素排序与拆 `DesktopBootstrap` 均留后续。
- `docs/architecture-standards.md` 与 `docs/coding-standards.md` 分层：前者管组织/结构/契约，后者管单段代码怎么写；`architecture.md` 保持描述现状，不重复。
- AGENTS.md 增「架构规范」入口行 + 预算表；doc-budgets manifest 增 `docs/architecture-standards.md`（≤600 词，实测 413）。
- **遗留（下一步，非本批）**：按新规范**拆 `DesktopBootstrap`/`DesktopBootstrap.Startup`**（组合根去业务、组件化）；清 4 处 `TODO(...)` 简化候选；StyleCop 元素排序（SA1201/02/04/14）由 suggestion 决定是否升 warning——均走实现模式 + feature-flow + 三重审核。
- 测试/构建/CI 不受影响（本批零代码、纯文档 + 门禁 manifest）；门禁（verify-adr-format/md-links/doc-budgets/cookbook/handoff/governance）应全绿。

## Related

- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：架构规范正文（本 ADR 落地文档）。
- [docs/architecture.md](../../../../docs/architecture.md)：现状描述（描述性，非规范）。
- [docs/coding-standards.md](../../../../docs/coding-standards.md)：C# 编码规范（R5 分工）。对应 ADR [2026-08-30-csharp-coding-standard](2026-08-30-csharp-coding-standard.md)（implemented）。
- [README](../../../../README.md)/根 [AGENTS.md](../../../../AGENTS.md)：架构规范入口与预算。
