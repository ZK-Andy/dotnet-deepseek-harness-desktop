# Agent Note: ADR 命名规则机器化 + 实现阶段踩坑记录分层

Status: implemented

## Problem

两套「靠自觉」的纪律暴露缺口：

1. **ADR 命名规则无机器校验**。`.agents/AGENTS.md` 与模板已定义路径形态（`<lifecycle>/<class>/yyyy-mm-dd-<topic>.md`、class 封闭集、状态即目录），但 `verify-adr-format.py` 只校验**文件内容**（头块/Status 与目录一致/骨架/禁用词），**不校验文件名格式**。2026-08-27 实况：该批 ADR 日期初次误写（08-26 vs 08-27）靠人工改名修正，门禁全程不拦——命名违规只会在归档审计等人工环节被发现。审计 57 篇现状命名全部合规（kebab-case、日期合法、class 合法），说明约定被遵守，但无任何机器兜底，一旦违约无信号。

2. **实现阶段踩坑记录无分层结构、且无机器校验**。踩坑散在三处：HANDOFF Gotchas（36 条，产品/环境/脚本判别经验，无阶段标签）、脚本内注释（单点坑，如 `bundle-runtime-ci.sh` trim 段）、ADR Consequences（决策代价）。「实现阶段的踩坑」（写脚本/写 C#/跨平台/门禁期碰壁）与「调试判别的坑」（运行时见报错的归因）混在同一 Gotchas 列表，检索、统计、沉淀节奏均无依据——根 AGENTS.md 文档纪律「procedure → cookbook」的对应结构未落地。

## Decision

### A. ADR 命名规则机器化（verify-adr-format.py 扩展）

在现有内容校验基础上，对每个 notes 文件新增**文件名/路径校验**，违约即 FAIL（独立于内容校验）：

1. **路径段数**：`<lifecycle>/<class>/<name>.md` 三段（顶层 `README.md` 豁免）。
2. **lifecycle 段** ∈ {proposed, implemented, rejected}——与 Status 行一致（复用既有逻辑）。
3. **class 段** ∈ 封闭集 {feature, bug-fix, simplification, architecture, process, testing}——新增校验。
4. **文件名 `<name>` = `yyyy-mm-dd-<slug>.md`**：
   - 日期段：`\d{4}-\d{2}-\d{2}` 且为合法日历日（python `datetime.date` 可解析）；
   - slug 段：kebab-case（`[a-z0-9]+(-[a-z0-9]+)*`），禁大写/下划线/中文/特殊字符；
   - 禁「提升状态改名」（日期=首次提出日，迁移不改名——校验只查格式不查历史，改名应走 `git mv`）。
5. **日期合理性**：不晚于运行日 + 容忍（允许当天）；早于 1970 判 FAIL。

### B. 实现阶段踩坑记录 → 独立 docs/cookbook.md（版本修订）

**（修订：初版按「贴标签进 HANDOFF Gotchas」实施；后发现 HANDOFF 是 gitignore 的本地工作文档、不进 git 也不进 CI，阶段标签约定无任何机器兜底——改判为落一个**进 git、可门禁校验**的独立 cookbook。）**

踩坑（implementation pitfalls）与调试判别经验落单一事实源 `docs/cookbook.md`（对应根 AGENTS.md「procedure → cookbook」），每条带**阶段标签前缀**：

```
- **[阶段] 主题（yyyy-mm-dd 来源）**：正文…
```

阶段标签封闭集（与 ADR class 对齐的流程语义）：
- `[脚本]` 写脚本/跨平台 shell/C# 编译器/门禁期踩坑（procedure）
- `[打包]` bundle/package/CI 打包链路踩坑
- `[调试]` 实机/DevTools/日志归因判别经验
- `[环境]` 沙箱/系统/运行时环境事实
- `[上游]` 依赖方行为与等待项
- `[产品]` 用户可见行为约定

格式由 `scripts/verify-cookbook.py` 机器强制（违约即 FAIL）：每条必须以合法阶段标签开头、标题区含合法日期（≤ 运行日）、stage 与所在 `## 阶段` 段一致、有非空正文；含 `--self-test` 离线自测。脚本级单点坑继续就近留脚本注释（fail loud 提醒），ADR Consequences 继续承载决策代价——三者各司其职不重复。

### C. 归属边界

- 命名规则属流程纪律，落入 `verify-adr-format.py`（机器化）+ `.agents/AGENTS.md` 一句话规则（人读）+ 模板注释（创建时引导）——同 docs-management-tiering 第 2 层「治理仓」候选内容，当前项目级先行，触发条件命中再迁治理仓。
- 踩坑分层属 procedure → **`docs/cookbook.md`**（进 git、可门禁校验），不放 HANDOFF Gotchas（本地工作文档无门禁）；HANDOFF 不再承载踩坑内容，`.agents/AGENTS.md` 只留一行链接导航。

## Alternatives considered

- **命名规则仅文档化、不写门禁**：落败——2026-08-27 日期误写已实证「文档对了也会漏」，机器校验是唯一可靠闸门；同 AGENTS.md「每人只有一个家 + 机器可校验」原则。
- **踩坑贴标签进 HANDOFF Gotchas（初版方案）**：落败——HANDOFF 是 `.gitignore` 的本地工作文档，不进 git、不进 CI；阶段标签约定因此无任何机器兜底，与「机器可校验」原则冲突。改判为落独立 `docs/cookbook.md`。
- **生命周期改名时允许日期跟随现状**：落败——「日期=首次提出日」是既有约定（模板明示），改名只会毁掉决策时间线；校验格式不改历史。
- **label 用自由文本**：落败——封闭集才能被门禁与检索消费，自由标签会逐步退化出同义词（历史上自由格式的教训）。
- **归档审计（dsh-archive-agent-notes）顺带核命名**：落败——审计是人工低频环节，命名是每次新建的即时动作，闸门必须建在 verify-adr-format（每次新建/提交都跑）。

## Consequences

- 收益：命名违约即时 FAIL（新建即知）；踩坑记录进 git 可版本化、可检索聚合（如「脚本坑汇总」供写脚本前扫一眼）、可门禁校验；实现与调试两类坑不再互相淹没；HANDOFF 不再承载受保护的内容。
- 代价/风险：存量踩坑从 HANDOFF 迁到 cookbook 为一次性人工整理（低风险机械活）；class/命名规则收紧后，任何历史笔记若违约需先修命名再跑门禁（审计现状 58 篇全合规，零回填成本）。
- 实施顺序：A（门禁扩展+自测）→ B（独立 cookbook + verify-cookbook 门禁）→ `.agents/AGENTS.md` 链接 + HANDOFF 移除 Gotchas。

## Testing

- `verify-adr-format.py --self-test`：6 用例（合规树 / 非法 class / 文件名大写 / 未来日期 / 非法日历日 / 路径段数不足）全部通过，违约样例正确 FAIL、合规样例 PASS。
- `verify-adr-format.py`（真实树）：58 篇 Agent Notes 全部通过，零命名违规。
- `verify-cookbook.py --self-test`：7 用例（合规 cookbook / 非法 entry stage / 未来日期 / 非法日历日 / stage 与段不匹配 / 空正文 / 未知阶段标题）全部通过。
- `verify-cookbook.py`（真实 cookbook）：40 条踩坑全部通过，阶段标签封闭集 + 日期合法。
- 门禁（verify-adr-format / verify-cookbook / verify-doc-budgets / verify-md-links）全绿；`.agents/AGENTS.md` 185/300、`.agents/notes/README.md` 191/800 字数预算均未超限；`dotnet build` 0 警告、`dotnet test` 308/308。

## Related

- [2026-08-25-docs-management-tiering](../../proposed/process/2026-08-25-docs-management-tiering.md)：框架级治理仓的前置分析，本决策同属「流程纪律」，其上第 2 层候选。
- [2026-08-20-initial-ai-devops-adaptation](../../implemented/process/2026-08-20-initial-ai-devops-adaptation.md)：首批流程卡与门禁骨架，verify-adr-format 扩展是其后续演进。
