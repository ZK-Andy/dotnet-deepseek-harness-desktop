# Agent Note: gate-common-shared-module（F 批：Python 门禁共享模块 + 最薄两门自测）

Status: implemented

Review: FULL/2026-09-29/R1=ok R2=ok R3=ok

中文（双语暂不启用）。

## Problem

结构整改总纲（`process/2026-09-27-post-packaging-churn-restructure`）F 批点名的脚本层重复在 15 个 Python 门禁脚本（约 5200 行）里持续增殖：

- **repo-root 发现多派**：`Path(__file__).resolve().parent.parent`（ui-copy/skill-format）vs `parents[1]`（governance）vs 根经 argv 传入（md-links 的位置参数）——scripts/ 挪家时要分开修三处。
- **Markdown slug/链接模型两份逐字拷贝**：`slugify`/`heading_slugs`/`HEADING_RE`/`ANCHOR_RE`/`LINK_RE` 在 verify-md-links 与 verify-skill-format 各一份；GitHub slug 规则一处改另一处必漂。
- **C# 去注释两份实现**（方案原文称三份，实查第三份 verify-ui-copy 剥的是 JS 注释——勘误）：verify-code-health 的 `_LineScanner` 全功能（行/块注释 + 普通/逐字/插值字符串 + char 字面量），verify-code-conventions 的 `_strip_comments_line` 只剥注释、**不识别字符串**——字符串里的 `//`（如 URL 字面量）会把该行剩余部分误当注释截掉，是现成的假阴性通道；反之字符串含 `Console.Write` 也会假阳性。迁移实查还发现 health 版自身有一处现行错误：普通字符串分支把开引号位置原样传入 `_skip_string`，扫描循环第一步就命中开引号立即返回，**普通字符串内容从未被剥除**（docstring 声称会）——内容漏成代码，字符串里的 `//` 同样截行（`scripts/verify-code-health.py:100` 的调用约定对照 `_skip_string` 循环体可复现：`var u = "http://x"; File.Exists(u);` 扫成 `var u =  http:`）。
- **自测 idiom 三份拷贝**：`def ok(cond, msg)` 闭包 + `failed` 计数 + 结尾横幅在 coverage-summary/readme-badges/review-tier 逐字三份。
- **门禁里最薄的两环没有自测**：verify-doc-budgets 与 verify-md-links 无 `--self-test`，而两门 pre-commit 与 CI 都跑；判据回归无离线夹具兜底。
- 另 verify-code-conventions 里 `D004_WHITELIST` 连写两遍（死重复）。

F 批第三项「覆盖率比对数」已由 B-1（`process/2026-09-28-ci-gate-honesty`）先行交付：coverage-summary.py `--baseline` + 0.5pp 容差，ci.yml 已接线——不在本批。

## Decision

1. **`scripts/gate_common.py` 为 Python 门禁共享模块**（shell 侧 `scripts/lib/` 单源纪律的 Python 对应物；模块不带可执行位），收五样：
   - `repo_root()`——唯一发现逻辑（`parents[1]`）；
   - `slugify`/`heading_slugs`/`HEADING_RE`/`ANCHOR_RE`/`LINK_RE`——Markdown slug 与链接语法单源；
   - `CSharpLineScanner`（原 health `_LineScanner` 迁出，全功能版为基准）——health 计大括号与 conventions 契约扫描共用；
   - `SelfTest`——ok/failed/横幅 idiom 单源；
   - 自带 `--self-test`（slug/扫描器字面量/自测 idiom 的离线夹具）。
2. **conventions 扫描换 `CSharpLineScanner`**：字符串字面量内容被剥除，修掉「字符串内 `//` 截行」假阴性通道；共享版同时修正 health 版的普通字符串开引号约定（从 `i + 1` 起扫，内容真正剥除，与其 docstring 声明一致）；gate_common 自测与 conventions 自测各加夹具锁该行为（字符串含 `//` 时行内后续 `File.` 仍须命中 D005）。
3. **最薄两门补自测**：verify-md-links 抽出 `check_tree` 供 main/自测共用（夹具盖好链/缺目标/死锚/排除面）；verify-doc-budgets 抽出 `check`（夹具盖超限/缺文件/代码块与表格不计词/manifest 缺失 SKIP）。
4. **自测接线 CI**：ci.yml 新步跑 `gate_common --self-test` + 两个新自测（离线夹具，每次 push 执行）。
5. **采纳面**：md-links/skill-format/governance/ui-copy/health/conventions 六脚本改 import；coverage-summary/readme-badges/review-tier 的逐字 `ok()` 换 `SelfTest`；删 conventions 的重复 `D004_WHITELIST`；`scripts/__pycache__` 本地残留删除（.gitignore 已盖）。
6. **「先拆再上」连带项**：扫描器修正让 `verify-code-health --enforce` 在现行树上暴露一条被旧 bug 掩盖的真实 F2（`DesktopTrayCommandRouter.cs:62` `RouteAsync` 113 行 > 80——字符串内容漏成代码曾使大括号配对错位、方法跨度少算）。按 architecture-mechanization 强制规则在批内拆解：抽 `ParsePayload`（静态）与 `CheckUpdateInBackgroundAsync` 两个私有方法，纯搬移零行为变更，`RouteAsync` 收至 70 行（`verify-code-health` span 口径），F2 解除；既有路由测试（记序/帧/日志契约）全绿。
7. **评审连带的两处方言修复**（R2 探针实证，均系 base 版 health 逐字带入、现树零命中）：`_skip_char` 普通形态（`'x'`）曾吞掉行尾（else 分支步进落在内容字符、闭引号判定落空即 `return n`），改为步进落在闭引号；`$"..."` 插值串曾误用逐字方言（合法的 `\"` 转义提前终止扫描、余量漏成代码），改走 escape-aware 分支并删除随之失去调用方的 `raw` 参数。两形态各补自测夹具锁死。

## Alternatives considered

- **连 review-tier/review-brief 一起拆 `review_gate_common.py`**（§5.4 的建议）：两脚本合计 1595 行、边界靠 docstring 与互调维系，拆分是独立风险面，0.5 天批装不下——挂后续批，本批只把 review-tier 的逐字 `ok()` 换用共享 idiom。落败（本批不做）。
- **`_scan`/`_violations`/`def main`/`_self_test` 也抽共享**：同名不同义（签名与语义各异），强行统一把无关门禁耦进同一抽象。落败。
- **ui-copy 的 JS 去注释并入 `CSharpLineScanner`**：语言不同（client.js），合并是错误抽象。落败。
- **conventions 保留自己的瘦扫描器**：保留即保留假阴性通道；统一到全功能版顺带修真 bug。落败。

## Consequences

- 判据单源：slug 模型/C# 字面量模型/自测 idiom 各一处实现；scripts/ 挪家只改 `repo_root()`。
- conventions 对字符串字面量更严（字面量内的 D004/D005 命中清零）——扫描面向真实语义对齐；src/ 实跑由 `--enforce` 门禁与 CI 验证。
- gate_common/md-links/doc-budgets 三处自测进 CI，最薄环节有离线回归锁。

## Testing

- `python3 scripts/gate_common.py --self-test`、`verify-md-links.py --self-test`、`verify-doc-budgets.py --self-test` 及全部既有门禁自测逐个实跑绿（16 个 Python 门禁全量）。
- `dotnet test` 867/867 绿、build 0 警告（含 DesktopTrayCommandRouter 拆分后的路由契约测试：记序/帧/日志痕）。
- ci.yml 变更以 push 实跑验证（ci.yml 无 `workflow_dispatch` 触发器，push@main 即其执行路径；表达式错误只有真 runner 能暴露）：push run `36457713574` 五作业全绿，新步「Python 门禁自测」三条命令在真 runner 实跑通过。

## Deferred

- `review_gate_common.py`（review-tier/review-brief 共享面）拆分；其余 Python 门自测接线（现仅 ui-copy + 本批三处）。
- 路由后台腿的行为钉：`DesktopTrayCommandRouterTests` 现不传 `updateMachine`，`CheckUpdateInBackgroundAsync` 的通知路径无测试夹具（R2 挂账；测试面非本批 diff）。

## Related

- 总纲：`implemented/process/2026-09-27-post-packaging-churn-restructure`（F 批落地记录；总纲随 G 批完成后迁 implemented）
- 覆盖率比对数的家：`implemented/process/2026-09-28-ci-gate-honesty`
