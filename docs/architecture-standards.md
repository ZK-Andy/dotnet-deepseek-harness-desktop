# 架构规范（Architecture Standards）

> 本项目架构**规范**（规则/强约束，应如何组织）。**单一事实源 = 本文件**；「现状描述」见 [architecture.md](architecture.md)（它是描述，非规范）；「踩坑判别」见 [cookbook.md](cookbook.md)；「项目规则」见根 [AGENTS.md](../AGENTS.md)。

## 基准与来源

- 官方权威：Microsoft [.NET Application Architecture guides](https://dotnet.microsoft.com/en-us/learn/dotnet/architecture-guides) 与 [Architect modern web applications with ASP.NET Core and Azure](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/)。
- 同一原理的不同命名：[Clean Architecture (Uncle Bob)](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)、[Onion (Jeffrey Palermo)](https://jeffreypalermo.com/blog/the-onion-architecture-part-1/)、[Hexagonal / Ports-and-Adapters (ploeh：*Layers, Onions, Ports, Adapters: it's all the same*)](https://blog.ploeh.dk/2013/12/03/layers-onions-ports-adapters-its-all-the-same/)。

**采纳原理，不照抄模板**：本项目是**单进程桌面壳**（Ryn + dsh 运行时），非 web 应用/微服务。**不采用** Clean Architecture 的「Domain / Application / Infrastructure / Presentation 分项目」模板——对该规模属过度设计；**采纳**其底层原理（分离关注点、依赖方向、依赖倒置/端口-适配、组合根、可测性），并按本应用形状落地。

## 应用概览（逻辑分层）

单项目 `DeepSeek.Harness.Desktop`，以逻辑层（命名空间/文件夹）组织。**单项目 + 文件夹若不控依赖方向会退化为意大利面（官方指引明确点名）——本规范的核心就是给「谁依赖谁」立规。**

| 层 | 职责 | 示例 | 依赖方向 |
|---|---|---|---|
| 组合根 | 装配、启动、接线、兜底 | `DesktopBootstrap` / `DesktopBootstrap.Startup` | 依赖各层（**只装配**） |
| 应用/域逻辑 | 用例、编排、状态机、监督 | `Services/RuntimeSupervisor`、`PageHealthMonitor`、`Services/Update/*`、`MarketInstallHelper` | 依赖**抽象/接口**，不碰基础设施细节 |
| 子域 | 相对独立的收拢面 | `Services/Update/`、`Services/Tray/` | 自成一体，经接口对外 |
| 外部边界（Ports/Adapters） | 与外部世界交互的实现 | Ryn webview/native、dsh Node 进程、companion 插件 IPC、文件系统/网络、更新 feed、注册表/rc | **实现**内层接口，由组合根注入 |

## 规则

### R1 · 组合根纪律
`DesktopBootstrap`/`DesktopBootstrap.Startup` 只做装配、启动、接线与兜底；**不承载业务/领域逻辑**。新增功能一律进 `Services/` 单职责组件（或对应子域），组合根只负责实例化/注入/编排。组合根方法应薄；新逻辑「塞不进 Services/」即触发重构信号，而非继续膨胀组合根。

### R2 · 依赖方向
- 依赖**指向内层**：应用/域层不依赖外部边界实现；外部边界**实现**应用层定义的接口。
- **禁止循环依赖**（跨层互相 `new`/引用）。
- 上层经**接口**调下层；下层实现**不向上**暴露实现细节。

### R3 · 外部边界抽象（Ports/Adapters）
- 与外部交互（Ryn/native、dsh 进程、companion IPC、文件/网络、更新 feed、注册表/rc）须经**接口**抽象：接口定义在应用层，实现进边界组件，组合根注入。
- 测试时该边界注入 fake/mock（与既有 mock 策略一致）；纯逻辑（状态机/编排）保持可单测，不触真实基础设施。
- **IPC/帧契约**：跨界 ID 用强类型/Branded（禁裸 `string` 跨包）；帧形状经 `AppJsonContext` 源生成（AOT 安全）；帧契约演进须留痕。

### R4 · 反上帝对象 / 尺寸健康闸（健康提示，非编码规范）
- 组件超 ~400 行、方法超 ~80 行、组合根内含业务逻辑 → **评审/门禁提示**。
- 定位为「开发完成即检」的**健康检查**，不是「编码规范」——规范不设行数上限（见 [coding-standards.md](coding-standards.md)），避免「方法≤30 当规范」被否的口径冲突。

### R5 · 跨切面行为契约
- **async/取消**：`async` 方法以 `Async` 结尾；`CancellationToken` 贯通（传入被取消链路）；禁 `async void`；库/非 UI 上下文用 `ConfigureAwait(false)`。
- **异常策略**：fail loud——缺失/误配置在**最早可解析点**失败；空 `catch` 必须命名所吞异常；`try` 只包一个语句；只捕获能妥善处理的异常。
- **日志**：统一经 `HostLog`（stdout + `<home>/logs/host.log` 双写）；重要状态迁移/失败原因留痕（对齐可观测性）。

### R6 · 与 coding-standards 的分工
- 本文件管**组织/结构/契约**（层、依赖方向、边界、行为契约）；[coding-standards.md](coding-standards.md) 管**单段代码怎么写**（格式/命名/惯用，机器强制）。
- 不重复 coding-standards 已强制的内容；冲突时以各自聚焦面为准。

## 相关

- 现状描述 [architecture.md](architecture.md)；踩坑 [cookbook.md](cookbook.md)；项目规则 [AGENTS.md](../AGENTS.md)。
- 决策与取舍（含 Alternatives）见 ADR [2026-08-30-architecture-standards](../.agents/notes/implemented/process/2026-08-30-architecture-standards.md)。
