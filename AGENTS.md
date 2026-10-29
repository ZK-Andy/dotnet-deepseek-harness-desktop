# dotnet-deepseek-harness-desktop — 项目规则

DeepSeek Harness Desktop for .NET：DeepSeek Harness 的 .NET 桌面客户端（NuGet 命名空间 `DeepSeek.Harness.Desktop`）。
本文件与 `.agents/` 定义 AI 协作骨架；产品面（.NET 桌面壳、打包、CI）见 `docs/` 与 README。

## 反馈纪律（置顶）

- **不奉承、不附和**：禁绝开场/收尾的夸奖与认同式措辞（「照办」「好问题」「你拍板」等）；不因用户已决定就收窄异议。开场直给结论与代价。
- **用户判断有缺陷即当场指出**：先说缺陷、后果与被放弃的备选，再执行其决定；把「执行」与「认同」分开——执行不等于同意。
- **执行后标残留代价**：决定落地后单列不可恢复项与已继承的债（永久红的 CI run、不可编译的历史提交等），不与「已完成」并表。
- **自己的方案也要自曝缺口**：方案里的可行性主张（如「逐片推送可行」）必须先验证再报价；未验证即视为待证项，不得作为选型依据。

## 协作模式（AI + 人）

- 本仓库由 coding agent（DeepSeek Harness）+ 人协作开发；agent 先读本文件与本仓库技能再动手。
- 每次动手前说清变更范围；**非平凡变更必须同变更携带 Agent Note（ADR）**。ADR 的路径/生命周期/格式/维护细则（含强制 `## Alternatives considered`、`rejected`/`archived` 纪律、Erratum 通道）的**单一事实源**是 [.agents/notes/README.md](.agents/notes/README.md)。

## 流程卡（索引）

会话与开发流程按卡执行：

- [session-modes](.agents/workflows/session-modes.md)——模式契约：讨论/调研/实现/发布的许可边界；**会话开场必须声明模式**
- [session-open](.agents/workflows/session-open.md) / [session-close](.agents/workflows/session-close.md)——会话开、收尾检查单
- [feature-flow](.agents/workflows/feature-flow.md) / [release-flow](.agents/workflows/release-flow.md)——开发与发版主链路

## 文档纪律

- **每个事实只有一个家**：rationale → Agent Notes；procedure → cookbook；contract → README；规则 → 本文件 + 链接。当前已知单一家分配：架构/启动模型、运行时来源、插件装配 → [docs/architecture.md](docs/architecture.md)；运行/门禁操作步骤 → [docs/development.md](docs/development.md) + [docs/testing.md](docs/testing.md)；测试基线数字 → `scripts/test-baseline.json`（ADR 只引用不复制）。
- durable 文档**写当前状态，不写变更历史**（"previously / now / no longer / renamed" 是 slop）。
- 相对 Markdown 链接 + 机器可校验；禁裸文件名引用。

## 编码约定

- **脚本规范**：`scripts/**` 与 `.githooks/**` 的编写纪律见 [docs/script-standards.md](docs/script-standards.md)（基线 = Google Shell Style Guide + ShellCheck + actionlint；机器强制面 S1–S6 由 `verify-shell-standards.sh` 执行；共享库唯一家在 `scripts/lib/`）。
- **C# 编码规范**：基准见 [docs/coding-standards.md](docs/coding-standards.md)（dotnet/runtime C# Coding Style + Microsoft .NET C# Coding Conventions；仓库根 `.editorconfig` 已落地，IDE/Roslyn 据此格式化与提示；CI/build 门禁按 `.editorconfig` 强制格式与风格——见该文档「强制力度」）。
- **架构规范**：系统怎么被组织（层/依赖方向/边界）见 [docs/architecture-standards.md](docs/architecture-standards.md)；装配面只装配、外部边界经接口、反上帝对象健康闸——非平凡结构变更按此执行。
- **行为契约**：async/取消、异常、日志约定见 [coding-standards](docs/coding-standards.md)「行为契约」节（fail loud、空 catch 命名、`try` 只包一个语句、`HostLog` 单点等细则单一事实源在彼，不重复）。
- 可调参数进配置模型（Config/appsettings），禁止硬编码；协议常量与安全不变量保持固定。
- **新规范默认问「能不能进 verify/analyzer」**：机器可查的不变量必须接进可执行门禁（门禁脚本或 Roslyn analyzer——编译器内门禁优先）；机器化不了的才落「评审检查项（AI 兜底）」，且该清单只减不增（已机器化的条目随激活移出）。
- 跨界 ID 用强类型/Branded，禁止裸 string 跨包传递。
- 公共 API 带 XML doc 契约（`<summary>/<param>/<returns>`）。
- 测试：`dotnet test`（xunit）；覆盖边界、错误路径、事件顺序、并发；**行为级变更必须配套回归/快照**；mock 只用于昂贵/非确定性边界。
- **DSH 插件命名**：`dsh-<域>-<角色>` 短 token（先例 `dsh-desktop-companion`）；禁止 `-plugin-` 类中缀；注册前查 npm 与 dshmarket 占用。

## 评审检查项（AI 兜底）

以下**「留评审/语义」规则机器无法强制**，由三重审核代理在**仓库约定**下额外执行——上游 dsh-code-review 技能只含通用清单、不含本项目规则，须显式补审（见 [feature-flow](.agents/workflows/feature-flow.md) 步骤 5）。编码风格类（`Async` 尾缀、`async void`、无类型空 `catch`）已机器化进 build 门禁（VSTHRD200 / VSTHRD100 / DSH0001），不在此列。

- **R1 装配面只装配**：`Hosting/AvaloniaHost`/`Program` 不得夹带业务/领域逻辑（[architecture-standards](docs/architecture-standards.md) R1）。
- **R3 边界抽象完备**：外部交互（native/WebView、dsh 进程、companion IPC、文件/网络、更新 feed、注册表/rc）是否都经接口、未直漏进业务层（R3）。
- **IPC 强类型**：跨界/事件帧 ID 不用裸 `string` 跨包；帧形状经源生成上下文（`AppJsonContext`/`UpdateJsonContext`，R3）。

评审代理按上述清单核对；发现即作 blocker 或 suggestion。

## GitHub 调研纪律（强制）

调研 GitHub 项目一律 gh CLI；禁以 web 检索开局、禁全量克隆作首选。六步配方见 [.agents/workflows/github-research.md](.agents/workflows/github-research.md)。

## 检索通道路由（强制）

web 检索一律 anysearch 插件（唯一搜索后端，已 Provider 级接管）；GitHub 归 gh。路由表见 [.agents/workflows/search-routing.md](.agents/workflows/search-routing.md)。

## 工具调用纪律（强制）

探索性命令必须合并为单次复合调用；结构未知时最多两跳（一次 dump 结构、一次按结构取数）。禁止「猜一层、看一层」的串行探测：每次工具结果与中间判断都永久留在上下文里，拆得越碎占用涨得越快。本纪律覆盖本机文件排查、插件检查、日志定位等一切探索，不限于 GitHub 调研——gh 场景的截断与并行细则见 [.agents/workflows/github-research.md](.agents/workflows/github-research.md)。门禁无法校验此项（机器看不到工具调用形态），违反即视为工具使用缺陷。

## Git 纪律

- **等待纪律**：任何 shell 等待单次 ≤ 30 秒；等 CI 等长事件用 `gh run watch` / 间隔轮询，期间并行做收尾作业，禁止长 sleep。

- 改写历史必须 `--force-with-lease=<branch>:<observed-oid>`；**raw `--force` 永远禁止**；改写后重新审计评审状态。
- push 前最小证据：按 diff 面选最窄检查（先用 `scripts/change-scope.sh`）；禁止默认全量跑、禁止为掩盖未覆盖文件收窄覆盖率。
- hooks 只做快检查，CI 拥有穷尽矩阵。

## 质量门

门禁清单、每道闸的判据、以及「`hooks` 快检查 / `CI` 穷尽矩阵」的分档，唯一事实源是 [docs/testing.md](docs/testing.md)。push 前的最小证据集按 [dsh-pre-push-checks](.agents/skills/dsh-pre-push-checks/SKILL.md) 选取，禁止默认全量跑。

## 字数预算

上限清单与判据的唯一实现在 [`scripts/doc-budgets.manifest.json`](scripts/doc-budgets.manifest.json)（本文件亦在其中，≤ 800 词）。改额走 manifest 并附 `_justify_bump`；超限先迁移到其他层（留一行链接）→ 精简 → 才提额度（PR 说明理由）。

## 参考

- 体系方法论文档与交接文档家庭（入口/待办/冷归档卷/journal 卷）均为**本地工作文档，未纳入 git 提交**（由 verify-handoff-structure 在存在时校验）；克隆后无这些文件属预期，会话开收尾按语义步骤执行，不依赖路径可达。
- 本项目 `.agents/skills/` 为自研技能（方法论吸取上游 deepseek-harness）；原版 11 技能备份于 git 之外的本地备份目录。技能规范见 `.agents/AGENTS.md`。
