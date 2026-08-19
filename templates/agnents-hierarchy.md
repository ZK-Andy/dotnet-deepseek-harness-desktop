# AGENTS.md 分层模板（适用任意语言/任意平台项目）

> 原则：**每个事实只有一个家**；根文件只放"每次会话都需要的常驻命令"（每条 1-3 行 + 链接）；详情放专属文档，由 agent 按需读取。
> 用法：把下面各节内容填入对应文件，删掉不需要的节；按 `## 字数预算` 表为每份文件定上限。

---

## 1. 根 AGENTS.md（项目根，≤ 800 词）

```markdown
# <项目名> — 项目规则

<一句话项目定位 + 架构说明，指向架构文档>

## 命令

```sh
<build / test / lint / typecheck 命令>
<每个命令一句话说明>
```

## 约定

- <惯例 1：一句话，链接详情>
- <惯例 2>
- <惯例 3>

## 文档纪律

- 非平凡变更必须同 PR 携带 Agent Note（见 .agents/notes/README.md）
- 文档写当前状态，不写变更历史（"previously/now/no longer" 是 slop）
- 每个事实只有一个家：rationale → Agent Notes；procedure → cookbook；contract → README

## 质量门

- <门禁命令：verify-* 或等价物>
- <CI 分工：hooks 只做快检查，CI 拥有穷尽矩阵>
```

## 2. 子树 AGENTS.md（可选，每子树 ≤ 300 词）

`packages/AGENTS.md`、`docs/AGENTS.md` 等，只写该子树专属规则，不重复根文件内容。

```markdown
# AGENTS.md — <子树名>

<该子树专属规则，每条 1-3 行 + 链接>
```

## 3. Agent Notes 目录骨架（.agents/notes/）

```
.agents/notes/
├── AGENTS.md        # 笔记系统规则（引用模板 docs/方法论提炼.md §2.2）
├── README.md        # 笔记规则：分类/时机/格式/归档
├── proposed/        # 提案（评审中）
│   └── <class>/yyyy-mm-dd-<topic>.md
├── implemented/     # 已上线决定（现在时，随代码保持最新）
│   └── <class>/yyyy-mm-dd-<topic>.md
├── rejected/        # 被拒（仅当理由能防重蹈覆辙时保留）
└── archived/        # 冻结历史（永久只读）
```

模板骨架见 `templates/adr-proposed.md`、`templates/adr-implemented.md`。

## 4. 字数预算（示例表）

| 文件 | 上限 |
|---|---|
| 根 AGENTS.md | ≤ 800 词 |
| 架构文档 | ≤ 1200 词 |
| 子树 AGENTS.md | ≤ 300 词 |
| 文档标准 | ≤ 800 词 |

门禁：`scripts/verify-doc-budgets.py`（manifest 驱动）。超限时：① 迁移到其他层（留一行链接）→ ② 精简 → ③ 才允许提额度（PR 说明理由）。

## 5. 其他 agent 规则文件

- `CLAUDE.md` → symlink 到 `AGENTS.md`（Claude Code 兼容，单一事实源）
- `.claude/skills/`、`.agents/skills/` 等技能放置见 `docs/ADAPTATION.md` §1