# dotnet-deepseek-harness-desktop — 项目规则

DeepSeek Harness Desktop for .NET：DeepSeek Harness 的 .NET 桌面客户端（NuGet 命名空间 `DeepSeek.Harness.Desktop`）。
当前处于**通用适配（第一步）**：本文件与 `.agents/` 定义了与技术栈无关的 AI 协作骨架；`.NET` 项目初始化、README、CI 属第二步。

## 协作模式（AI + 人）

- 本仓库由 coding agent（DeepSeek Harness）+ 人协作开发；agent 先读本文件与本仓库技能再动手。
- 每次动手前说清变更范围；**非平凡变更必须同变更携带 Agent Note（ADR）**（见 `.agents/notes/README.md`）。
- 讨论与取舍落成 ADR，不散在会话里；ADR 强制 `## Alternatives considered`（不记录比赢过什么的决定，必然招致重新辩论）。

## 文档纪律

- **每个事实只有一个家**：rationale → Agent Notes；procedure → cookbook；contract → README；规则 → 本文件 + 链接。
- durable 文档**写当前状态，不写变更历史**（"previously / now / no longer / renamed" 是 slop）。
- ADR 路径即元数据：`{lifecycle}/{class}/yyyy-mm-dd-<topic>.md`；`rejected` 仅当理由能防重蹈覆辙才保留；`archived` 永久冻结。
- 相对 Markdown 链接 + 机器可校验；禁裸文件名引用。

## 编码约定（第二步 .NET 落地后强制执行）

- **fail loud**：缺失引用、误配置绝不静默跳过；最迟在最早可解析点失败。
- 可调参数进配置模型（Config/appsettings），禁止硬编码；协议常量与安全不变量保持固定。
- 跨界 ID 用强类型/Branded，禁止裸 string 跨包传递。
- 空 `catch` 必须命名它吞掉什么；`try` 只包一个语句。
- 公共 API 带 XML doc 契约（`<summary>/<param>/<returns>`）。
- 测试：`dotnet test`（xunit）；覆盖边界、错误路径、事件顺序、并发；**行为级变更必须配套回归/快照**；mock 只用于昂贵/非确定性边界。

## GitHub 调研纪律（强制）

调研 GitHub 项目一律 gh CLI，禁止以 web 检索开局、禁止全量克隆作首选：

1. 发现：`gh search repos "<词>" --sort stars --json fullName,description,stargazersCount,pushedAt`
2. 验真：`gh api repos/<r>`（创建日期 vs 星标、贡献者数、fork 比）
3. 内容：README `gh api repos/<r>/readme --jq .content | base64 -d | head -c 2000`；结构 `gh api repos/<r>/git/trees/HEAD?recursive=1 --jq '.tree[].path'`
4. 单文件：`gh api repos/<r>/contents/<path>`
5. 克隆仅当需跨文件 grep/跑代码，且必须 `git clone --depth 1 --filter=blob:none --sparse` 后 sparse-checkout 目标目录
6. 任何输出超约 50 行先 head 截断再进上下文；独立查询合并到同一次 bash 并行执行

## 检索通道路由（强制）

AnySearch 是唯一搜索后端（插件已接管内置 `web_search`/`web_fetch` 的 Provider）；规则只区分工具面的选择：

1. GitHub 项目/仓库/代码 → gh CLI（见上节），不再重复搜索
2. 库/框架官方文档与用法 → `code.doc`（params.library 必填）
3. 真实代码实现示例 → `code.snippet`（params.repo/lang/path 过滤）或 `gh search code`
4. 一般 web 检索 → `general.general`（zone：中文 `"cn"`、英文 `"intl"`）；无区域/纵向诉求的快查可用裸 `web_search`（同后端简化面，仅 query/max_results）
5. 多个独立查询合并单次 batch 调用；输出超约 50 行先截断再进上下文

## Git 纪律

- 改写历史必须 `--force-with-lease=<branch>:<observed-oid>`；**raw `--force` 永远禁止**；改写后重新审计评审状态。
- push 前最小证据：按 diff 面选最窄检查（先用 `scripts/change-scope.sh`）；禁止默认全量跑、禁止为掩盖未覆盖文件收窄覆盖率。
- hooks 只做快检查，CI 拥有穷尽矩阵（CI 第二步接入）。

## 质量门（当前可执行）

```sh
python3 scripts/verify-adr-format.py     # ADR 头/骨架/状态-目录一致性
python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json # 字数预算
python3 scripts/verify-md-links.py       # 相对链接/锚点（skills/ 排除）
scripts/change-scope.sh [<base> <head>]  # 变更范围（评审/push 前置）
```

## 字数预算

| 文件 | 上限 |
|---|---|
| 本文件（AGENTS.md） | ≤ 800 词 |
| .agents/AGENTS.md | ≤ 300 词 |
| .agents/notes/README.md | ≤ 800 词 |

超限：迁移到其他层（留一行链接）→ 精简 → 才允许提额度（PR 说明理由）。

## 参考

- 体系方法论文档在本目录 `.plan/`（本地工作文档，未纳入 git 提交）：`适配方案.md`、`适配经验总结.md`、`dsh.txt`；交接见同层的 `HANDOFF.md`。
- 本仓库技能均原版源自 `deepseek-ai/deepseek-harness`（MIT），出处见 `.agents/AGENTS.md`。
