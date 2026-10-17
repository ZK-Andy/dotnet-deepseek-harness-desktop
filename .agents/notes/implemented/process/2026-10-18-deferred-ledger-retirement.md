# Agent Note: deferred-ledger-retirement

Status: implemented

## Problem

ADR 的 `## Deferred` 小节长期被当成第二本待办账：八篇 implemented 笔记里挂着十余条「未做项」，其中多数在记账当天之后已经失去意义——`Yupmoh/Ryn#92`（Windows 原生图像解码 0xC0000005）早已修复、`gate_common.py` 早已存在、relay 方案 C 与 frameless 的全屏/折叠两项本就写在同篇的 Alternatives/Consequences 里。其余条目靠「复访触发」维持存在，而触发条件本身依赖日后记得回来复核，实际上无人复访。与此同时唯一在册的待办之家 `HANDOFF-todos.md` 行动区是空的——账目错位：真正的行动面没有条目，笔记里却堆着永不关闭的「未做」。

## Decision

- `## Deferred` 不再作为挂账面。未做项只有两种合法去处：**① 现在做**；**② 写成 `## Consequences`/`## Decision` 里的现状陈述**（边界、已知偏离、上游无时间表的事实都属此类）。
- 被上游阻塞的事项只记**事实与依赖**（如 `Yupmoh/Ryn#102` 仍 OPEN、运行期导航仍走相对源页面），不记触发条件；上游落地后按新事实改写该行。
- 行动区唯一家仍是 [`HANDOFF-todos.md`](../../../../HANDOFF-todos.md)；笔记不承载待办。
- 本批落地：删除六篇笔记的 `## Deferred`（payload-smoke-probe、loopback-forward-proxy、runtime-handoff-adoption、relay-restart-client-module-collapse、gate-common-shared-module、frameless-uniform-caption-bar），两条仍成立的事实改写进各自 Consequences；右键「打开链接」篇由 `proposed/` 转 `rejected/`（理由：本仓库侧无可做变更，治本依赖 saucer#90，而 #90 挂在 #53 上无时间表）。
- 同批登记的门禁缺陷（**不在本批修**）：`verify-adr-format.py` 的 `STATUS_RE` 把 `— 理由` 并进状态捕获组，README 允许的 `Status: rejected — 理由` 因此恒判「状态与目录不一致」；故本次 rejected 篇的 Status 行只写状态词、理由另起一行。修它属门禁判据面，须单独走 FULL 评审，不夹在记账批里。

## Alternatives considered

- **保留 Deferred、逐条复核触发条件**：落败——触发条件就是「记得回来看」，与不挂账等价，却让 implemented 笔记读起来像未完成清单。
- **全删且不写现状陈述**：落败——`Yupmoh/Ryn#102` 的运行期导航限制、诊断包不收 `.dsh-pid` 都是当前行为事实；删掉后排查会重新发现同一件事。
- **把未做项搬进 `HANDOFF-todos.md` 行动区**：落败——这些项没有可执行触发（上游无时间表、无真机），进行动区即制造永不关闭的 `[ ]` 条目。
- **给 Deferred 加机器校验（每条必须带触发条件与复访日期）**：落败——形式化触发条件不产生复访行为，只增加写与读的成本。

## Consequences

- 笔记只写当前决策与现状；「未做」不再是笔记的状态。
- 模板 [`templates/adr-implemented.md`](../../../../templates/adr-implemented.md) 仍把 `## Deferred` 列为可选小节——可选不等于推荐，新笔记不得用它挂待办。
- 代价：上游阻塞事实失去统一的等待清单视图，需要时按关键词（如 `Yupmoh/Ryn#102`）在笔记里检索。

## Testing

- `python3 scripts/verify-adr-format.py`：Checked 218 全绿；`--self-test` 全绿（门禁脚本本身未改，缺陷见 Decision 末条）。
- `python3 scripts/verify-md-links.py`（921 个链接目标）、`verify-doc-budgets.py`、`verify-handoff-structure.py`（交接窗 24 条）全绿；`## Deferred` 在 `.agents/notes/` 零命中（模板除外）。
- 删除后交叉引用逐处核对：relay 的 Decision 改指 Alternatives、market-restart-adopt-first 的 Related 改称「推迟项」、window-edge-ring 三处全屏 gate 改指自身现状、payload-smoke 的 Testing 段改标上游修复事实。
