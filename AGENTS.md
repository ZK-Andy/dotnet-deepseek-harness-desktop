# dotnet-deepseek-harness-desktop — 项目规则

DeepSeek Harness Desktop for .NET：DeepSeek Harness 的 .NET 桌面客户端（NuGet 命名空间 `DeepSeek.Harness.Desktop`）。
本文件与 `.agents/` 定义 AI 协作骨架；产品面（.NET 桌面壳、打包、CI）见 `docs/` 与 README。

## 协作模式（AI + 人）

- 本仓库由 coding agent（DeepSeek Harness）+ 人协作开发；agent 先读本文件与本仓库技能再动手。
- 每次动手前说清变更范围；**非平凡变更必须同变更携带 Agent Note（ADR）**（见 `.agents/notes/README.md`）。
- 讨论与取舍落成 ADR，不散在会话里；ADR 强制 `## Alternatives considered`（不记录比赢过什么的决定，必然招致重新辩论）。

## 流程卡（索引）

会话与开发流程按卡执行：

- [session-modes](.agents/workflows/session-modes.md)——模式契约：讨论/调研/实现/发布的许可边界；**会话开场必须声明模式**
- [session-open](.agents/workflows/session-open.md) / [session-close](.agents/workflows/session-close.md)——会话开、收尾检查单
- [feature-flow](.agents/workflows/feature-flow.md) / [release-flow](.agents/workflows/release-flow.md)——开发与发版主链路

## 文档纪律

- **每个事实只有一个家**：rationale → Agent Notes；procedure → cookbook；contract → README；规则 → 本文件 + 链接。
- durable 文档**写当前状态，不写变更历史**（"previously / now / no longer / renamed" 是 slop）。
- ADR 路径即元数据：`{lifecycle}/{class}/yyyy-mm-dd-<topic>.md`；`rejected` 仅当理由能防重蹈覆辙才保留；`archived` 永久冻结。
- 相对 Markdown 链接 + 机器可校验；禁裸文件名引用。

## 编码约定

- **C# 编码规范**：基准见 [docs/coding-standards.md](docs/coding-standards.md)（dotnet/runtime C# Coding Style + Microsoft .NET C# Coding Conventions；仓库根 `.editorconfig` 已落地，IDE/Roslyn 据此格式化与提示；CI/build 门禁按 `.editorconfig` 强制格式与风格——见该文档「强制力度」）。
- **架构规范**：系统怎么被组织（层/依赖方向/边界/行为契约）见 [docs/architecture-standards.md](docs/architecture-standards.md)；组合根只装配、外部边界经接口、反上帝对象健康闸——非平凡结构变更按此执行。
- **fail loud**：缺失引用、误配置绝不静默跳过；最迟在最早可解析点失败。
- 可调参数进配置模型（Config/appsettings），禁止硬编码；协议常量与安全不变量保持固定。
- 跨界 ID 用强类型/Branded，禁止裸 string 跨包传递。
- 空 `catch` 必须命名它吞掉什么；`try` 只包一个语句。
- 公共 API 带 XML doc 契约（`<summary>/<param>/<returns>`）。
- 测试：`dotnet test`（xunit）；覆盖边界、错误路径、事件顺序、并发；**行为级变更必须配套回归/快照**；mock 只用于昂贵/非确定性边界。
- **DSH 插件命名**：`dsh-<域>-<角色>` 短 token（先例 `dsh-desktop-companion`）；禁止 `-plugin-` 类中缀；注册前查 npm 与 dshmarket 占用。

## 评审检查项（AI 兜底）

以下**「留评审/语义」规则机器无法强制**（机械化门禁未覆盖），由三重审核代理在**仓库约定**下额外执行——上游 dsh-code-review 技能只含通用清单、不含本项目规则，须显式补审（见 [feature-flow](.agents/workflows/feature-flow.md) 步骤 5）：

- **D001–D003**：`async` 方法名应含 `Async` 尾缀；非事件处理器的 `void` `async` 方法；`catch` 空体须命名所吞（[coding-standards](docs/coding-standards.md) 行为契约）。
- **R1 组合根只装配**：`DesktopBootstrap`/`DesktopBootstrap.Startup` 不得夹带业务/领域逻辑（[architecture-standards](docs/architecture-standards.md) R1）。
- **R3 边界抽象完备**：外部交互（Ryn/native、dsh 进程、companion IPC、文件/网络、更新 feed、注册表/rc）是否都经接口、未直漏进业务层（R3）。
- **IPC 强类型**：跨界/事件帧 ID 不用裸 `string` 跨包；帧形状经 `AppJsonContext` 源生成（R3）。

评审代理按上述清单核对；发现即作 blocker 或 suggestion。

## GitHub 调研纪律（强制）

调研 GitHub 项目一律 gh CLI；禁以 web 检索开局、禁全量克隆作首选。六步配方见 [.agents/workflows/github-research.md](.agents/workflows/github-research.md)。

## 检索通道路由（强制）

web 检索一律 anysearch 插件（唯一搜索后端，已 Provider 级接管）；GitHub 归 gh。路由表见 [.agents/workflows/search-routing.md](.agents/workflows/search-routing.md)。

## Git 纪律

- 改写历史必须 `--force-with-lease=<branch>:<observed-oid>`；**raw `--force` 永远禁止**；改写后重新审计评审状态。
- push 前最小证据：按 diff 面选最窄检查（先用 `scripts/change-scope.sh`）；禁止默认全量跑、禁止为掩盖未覆盖文件收窄覆盖率。
- hooks 只做快检查，CI 拥有穷尽矩阵。

## 质量门（当前可执行）

```sh
python3 scripts/verify-adr-format.py     # ADR 头/骨架/状态-目录一致性
python3 scripts/verify-cookbook.py       # 踩坑记录格式/阶段标签封闭集（docs/cookbook.md）
python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json # 字数预算
python3 scripts/verify-md-links.py       # 相对链接/锚点（skills/、archived/、.plan/ 排除）
python3 scripts/verify-handoff-structure.py # HANDOFF 滚动窗/状态区/归档指针（HANDOFF 存在即校验）
python3 scripts/verify-governance.py     # Issue/PR 模板治理字段（governance.yml 输入校验）
scripts/change-scope.sh [<base> <head>]  # 变更范围（评审/push 前置）
```

## 字数预算

| 文件 | 上限 |
|---|---|
| 本文件（AGENTS.md） | ≤ 800 词 |
| .agents/AGENTS.md | ≤ 300 词 |
| .agents/notes/README.md | ≤ 800 词 |
| docs/cookbook.md | ≤ 2500 词 |
| docs/coding-standards.md | ≤ 500 词 |
| docs/architecture-standards.md | ≤ 600 词 |

超限：迁移到其他层（留一行链接）→ 精简 → 才允许提额度（PR 说明理由）。

## 参考

- 体系方法论文档在 `.plan/`（本地工作文档，未纳入 git 提交）：`适配方案.md`、`适配经验总结.md`、`dsh.txt`；交接主文档在仓库根 `HANDOFF.md`（同为本地工作文档，不提交）。
- 本仓库技能均原版源自 `deepseek-ai/deepseek-harness`（MIT），出处见 `.agents/AGENTS.md`。
