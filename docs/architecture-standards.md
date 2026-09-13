# 架构规范（Architecture Standards）

> 本项目架构**规范**（规则/强约束，应如何组织）。**单一事实源 = 本文件**；「现状描述」见 [architecture.md](architecture.md)（它是描述，非规范）；「踩坑判别」见 [cookbook.md](cookbook.md)；「项目规则」见根 [AGENTS.md](../AGENTS.md)。

## 基准与来源（.NET 官方架构标准）

- 官方权威：Microsoft [.NET Application Architecture guides](https://dotnet.microsoft.com/en-us/learn/dotnet/architecture-guides) 与《[Architect modern web applications with ASP.NET Core and Azure](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures/)》第 2 章：单项目 + 文件夹分层随规模增长退化为意大利面（官方点名）；Clean Architecture 是**非平凡单体的标准解组织**。
- 官方参考实现：[dotnet/eShopOnWeb](https://github.com/dotnet/eShopOnWeb)（单体主链 ＝ `src/{ApplicationCore, Infrastructure, Web}`，另有 BlazorAdmin/PublicApi 等独立宿主），测试项目按层镜像。程序集/命名空间命名按 [Framework Design Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/names-of-assemblies-and-dlls)。
- **物理分层是硬要求**：只有多项目才能让依赖方向被编译器强制（命名空间扫描与白名单类补丁的退役路径见「强制与迁移」）。

## 目标解组织（官方三项目单体形态）

| 项目 | 官方层 | 内容 | 依赖 |
|---|---|---|---|
| `DeepSeek.Harness.Desktop.Core` | Application Core | 用例编排、状态机、监督策略、目录/预设、**端口（接口）**、域异常/守卫、IPC 帧契约 | **零外层引用** |
| `DeepSeek.Harness.Desktop.Infrastructure` | Infrastructure | 端口实现：dsh 进程、node/npm 引导、文件/注册表/UDS、更新 feed、CLI shim、托盘原生 | → Core |
| `DeepSeek.Harness.Desktop` | Presentation/组合根 | `Program`/`DesktopBootstrap*`、UI 桥（横幅/恢复页/托盘）、命令路由、ryn.json/appsettings | → Core + Infrastructure |

`tests/` 镜像拆分：Core 单测（纯逻辑，无基础设施 mock）；Infrastructure 边界集成/fake 测试；Ryn 宿主壳归 Presentation。

## 规则

### R1 · 组合根纪律
`DesktopBootstrap`/`Program` 只做装配、启动、接线与兜底；**具体基础设施类型只允许出现在组合根的 DI 注册处**。业务/领域逻辑进 Core，边界实现进 Infrastructure；新逻辑「塞不进 Core」即触发重构信号，而非继续膨胀组合根。

### R2 · 依赖方向（编译器强制）
- 依赖**指向内层**：Presentation → Core ← Infrastructure；**Core 的项目引用必须为空**（Application Core 不依赖任何外层）。
- 越界引用即编译失败；禁止循环依赖；上层经接口调下层，下层实现不向上暴露细节。`ArchitectureTests` 保留为项目引用断言 + 循环检测兜底。

### R3 · 端口/适配器（Ports/Adapters）
- 与外部世界交互（Ryn/native、dsh 进程、companion IPC、文件/网络、更新 feed、注册表/rc）一律：**接口定义在 Core，实现进 Infrastructure，组合根注入**；测试经 fake/mock（与既有 mock 策略一致）；纯逻辑保持可单测。
- **IPC/帧契约**：跨界 ID 用强类型/Branded（禁裸 `string` 跨包）；帧形状经源生成上下文（主工程 `AppJsonContext` / Core `UpdateJsonContext`，AOT 安全）；帧契约演进须留痕。

### R4 · 反上帝对象 / 尺寸健康闸（健康提示，非编码规范）
组件超 ~400 行、方法超 ~80 行、组合根内含业务逻辑 → 评审/门禁提示。定位为「开发完成即检」的健康检查；规范不设行数上限（见 [coding-standards.md](coding-standards.md)）。

### R5 · 与 coding-standards 的分工
本文件管**组织/结构/契约**（层、依赖方向、边界）；[coding-standards.md](coding-standards.md) 管**单段代码怎么写**（格式/命名/惯用，机器强制）+ **行为契约**（async/异常/日志）。不重复其内容，冲突时以各自聚焦面为准。

## 强制与迁移

- 机器强制优先级：项目引用（编译期）> `ArchitectureTests` 断言（A2/A4/A5）> D005 白名单（过渡期，物理分层完成后退役）。
- 迁移期单项目为**不合规过渡态**：新代码一律按目标层落位；拆分批次与台账见 ADR [2026-09-14-official-clean-architecture-adoption](../.agents/notes/implemented/architecture/2026-09-14-official-clean-architecture-adoption.md)。

## 相关

- 现状描述 [architecture.md](architecture.md)；踩坑 [cookbook.md](cookbook.md)；规则 [AGENTS.md](../AGENTS.md)。
- 决策与取舍（含 Alternatives）见 ADR [2026-09-14-official-clean-architecture-adoption](../.agents/notes/implemented/architecture/2026-09-14-official-clean-architecture-adoption.md)。
