# Agent Note: 组合根判据换闸——F3 集合计与 verify-compose-root 语义闸

Status: implemented

Review: FULL/2026-09-28#8/R1=ok R2=ok R3=ok

各路：R1 首轮 0 Blocker（4 Suggestion 全采纳：死常量删除/夹具自足化/共享挂账/微折叠）；R2 首轮 2 Blocker（C3 自有类型扫描面、目标类型 new() 漏计——计数跟值 9→15）+ 3 Suggestion，2 轮验轮闭合（含 1 处修复引入的 ADR 夹具数残留）；R3 首轮 0 Blocker（1 Suggestion 采纳：三处门禁描述补 C4）。

## Problem

形态分离（[compose-root-form-separation](../architecture/2026-09-28-compose-root-form-separation.md)）落地时留了尾巴：旧 F3 是**每文件** 400 行预算，正是「分部增殖」回归（12 天 +47%）的机器授权者——309 行的组合根集合计在旧判据下仍有充分的增殖空间，且「根里放了什么」依旧零机器覆盖：A5 退役后唯一盯「新功能塞组合根」的门不存在，编排/业务谓词落根只能靠评审兜底。

## Decision

判据从「文件多长」换成「根里放了什么」，两件工具：

1. **F3 改为集合计**（`verify-code-health.py`）：组合根集合（`Program.cs` + `DesktopBootstrap*.cs`）**总行数 ≤500**（`--compose-total-limit`，默认 500）；每文件 400 的 F1 对所有文件一视同仁保留。集合计直接命中分部增殖的回归形态——多开分部不再带来新预算。
2. **新增 `verify-compose-root.py`**（组合根语义闸，四检查）：
   - **C1** dot 分部数 ≤1（分部终态）；
   - **C2** 根内成员声明（方法/构造函数/表达式体成员）必须命中 `ALLOWED_METHODS` 清单（封闭集，随形态分离冻结为 9 个：Main/ExportDiagnostics/Run/ResolveRuntimeAndDev/AcquireSingleInstance/StartProxy/InitCloseGateAndUpdateStack/BuildApp/RegisterServices）——新成员落根即 FAIL，要么搬去 `Bootstrap/StartupSequence` 或域服务，要么显式更新清单（更新本身即决策点，须 ADR）；根文件识别用 rglob（子目录落位的新分部不脱检）；
   - **C3** 根内 `new` 自有具体域类型计数 ≤16（自有 = `--owned-src` 三工程内声明的类型；Ryn/BCL 不计；目标类型 `new()` 按同行声明类型归属，无法归属即报；当前 15——含 Core/Infrastructure 的 FirstBootBootstrapService/UiLocale/DesktopUiLocaleStore/CloseBehaviorPreference 与两个目标类型 `new()`，真实余量 1）；
   - **C4** 根内禁 `.GetAwaiter().GetResult()`（同步编排的旧病灶留在编排服务）。

接线：pre-commit（步骤 9a）+ ci.yml build-test job（独立 step）+ 根 AGENTS.md 质量门清单。`--self-test` 六夹具（合规/C2 新方法/C1 加分部/C3 超帽/C3 目标类型 new() 归属与不可归属/C4 同步编排）全过；actionlint 对 ci.yml 新 step 零告警。

## Alternatives considered

- **语义闸用 AST/Roslyn analyzer 实现**：语义最准，但 Roslyn analyzer 工程化成本高（另有 D003 analyzer 立项在先）；行级启发式 + 封闭清单已覆盖本批要拦的回归形态（新方法/新分部/new 增殖/同步编排），误报由清单显式更新消化。落败（暂）。
- **只改 F3 不加语义闸**：集合计挡总量，不挡「根里放什么」——把编排藏进 500 行内仍可行，正是 A5 退役后缺失的那道门。落败。
- **`new` 计数清单存 JSON 快照（只减不增的台账形态）**：与脚本分离的快照会腐化（跟值批先例）；直接以常量 16 为帽 + CI 输出当前计数（9），帽被突破时必然显式改脚本（FULL 面）。落败。
- **C2 清单用方法签名而非名字**：签名漂移（加参数）会制造维护噪音；名字封闭已足够——同名异义落根本来就该被拦。落败。

## Consequences

- 组合根的语义面首次有机器闸：新方法/新分部/new 自有类型超帽/同步编排四条回归路径在 pre-commit 即红。评审兜底清单里「R1 组合根只装配」保留（AST 级语义仍靠人），但机器先拦结构性增殖。
- 500/16/1 三个帽是**起点不是目标**：调低走脚本变更（FULL 面，天然带评审）。
- F3 判据变更使 `verify-code-health.py` 的 `--file-limit` 不再对组合根有独立含义（F1 仍统一适用）；旧的「compose 文件单独报 F3」行为删除。

## Testing

- `verify-code-health.py --self-test` 8 夹具全过（新增 F3 集合计超帽 + 提升帽后通过两组）；`--enforce` 实仓绿（集合计 309/500）。
- `verify-compose-root.py --self-test` 六夹具全过（含目标类型 `new()` 归属/不可归属两路）；`--enforce` 实仓绿（new-of-owned 15/16、dot 分部 1/1、C2 清单 9/9、C4 零命中）。
- `verify-governance.py`、actionlint（含 shellcheck -S warning）对 ci.yml/pre-commit 改动零告警；ci.yml 变更由 push 触发的 build-test 实跑验证。

## Related

- 前序：[compose-root-form-separation](../architecture/2026-09-28-compose-root-form-separation.md)（本闸是其 Consequences 声明的配套）。
- 规范：[architecture-standards.md](../../../../docs/architecture-standards.md) R1/R4；根 AGENTS.md「质量门」。
