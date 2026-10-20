# Agent Note: 两道新门禁的接线与扫描形态

Status: implemented

Review: FULL/2026-10-20#4/R1=ok R2=ok R3=ok

## Problem

审计整改第 4–6 项引入了两道新机器门禁：命令 ID 单一事实源闸（`scripts/verify-host-command-ids.py`）与
Core 纯度闸（`scripts/verify-core-purity.py`）。两者的**判据形态**（扫什么、什么算触点、怎么判红）
与**接线形态**（CI / pre-commit 各在哪跑）此前没有承载者——若散在各篇 ADR 的边角，读者查不到
「这两道门到底扫什么、误判边界在哪」。

## Decision

- **接线**：`ci.yml` 的 docs job 无条件跑两道闸（各带 `--self-test` + `--enforce`）；`pre-commit` 也各带
  `--enforce`（快检位）。`--enforce` 不可省——两道闸默认 report-only，CI 不带 flag 即恒绿（门禁不执行
  等于没有门禁）。
- **命令 ID 闸的判据形态**：① 已知 ID 的**整串**字面量不得出现在 Core 常量文件之外的 C#（含 tests）；
  ② 伴生插件 JS 与壳内引导页 HTML 里的命令 ID 形态字面量必须在已知集合内；③ 每个已知 ID 都要有引用面。
  扫描用 `gate_common.CSharpLineScanner`（剥注释、字符串内容留空）+ 行尾注释剥离（字符串内的 `//` 不算注释）——
  注释与字符串里的同形词不算触点；文件名形态（扩展名段）不属命令 ID。
- **Core 纯度闸的判据形态**：扫 Core 源文件的 IO 触点（文件/流/压缩/注册表/环境/临时路径/进程），
  禁用清单是**枚举式**的单点维护（新增 IO 形态须同步进清单，否则门有缺口）。
- **一次性豁免**：本批是审计整改第 4–6 项的**合并批次**——命令 ID 单一事实源、三处边界端口、
  Core 纯度下沉三条变更线互为前提（常量被路由引用、端口被注册面接线、纯度门覆盖前两者），
  拆成多波会留下「常量已加但门禁未上」「端口已换但适配器未接线」的半成品提交。故本批对增量闸
  做一次性豁免：`增量闸豁免：gross ≤ 2300，fanout ≤ 20`（本批实测 gross 2067 / fanout 16）。
  豁免由 `scripts/verify-increment-discipline.py --exempt <ADR>` 承载——**只有该 ADR 处于本次变更集内时
  标记才生效**（与评审证据的「本批新产出」同一思路）：批次落地后 ADR 进入基线，豁免自动失效，
  后续批次仍受默认帽（600 / 15）约束。
- **自测形态**：两道闸各带离线夹具（`--self-test`），覆盖合规样例与每条判据的反面（含「注释里的提及不算触点」
  「数据串内含 ID 不判红」「行尾注释里的带引号 ID 不判红」「块注释内的触点不判红」）。

## Alternatives considered

- **维持现状不修**（两闸只在本机手工跑、不进 CI）：落败——不进 CI 的门禁等于没有门禁；本仓的判据纪律是
  「机器可查的不变量必须接进可执行门禁」，且 CI 拥有穷尽矩阵（hooks 只做快检查）。
- **两道闸合进一个脚本**：落败——两条判据的扫描面与形态不同（ID 契约 vs IO 触点），合在一个脚本里会让
  失败原因互相掩盖（哪条判据红说不清），自测夹具也会互相污染。
- **把两道闸做成 C# 架构测试**（与既有 `ArchitectureTests` 同处）：落败——C# 侧没有仓内既有的
  「剥注释 + 剥字符串」扫描器可用（`gate_common.CSharpLineScanner` 是 Python 侧能力），手写一份等于把它
  降级重写（块注释跨行、verbatim 串、插值串都会误判）；脚本形态能复用同一份扫描器。
- **用 Roslyn 分析器实现这两道闸**：落败——命令 ID 闸的分析器侧拿不到 Core 常量集合（分析器与被分析程序集
  无编译期共享面），只能退回字面量模式匹配；Core 纯度闸需要「Core 允许的 API 白名单」，该白名单随 BCL 漂移，
  无稳定来源。

## Consequences

- 两道闸在 CI 与 pre-commit 各有一个执行点，`docs/testing.md` 的门禁表与执行点表同步登记。
- 判据边界是显式的：注释/字符串里的同形词不判红；文件名形态不属命令 ID；Core 纯度闸的禁用清单是枚举式，
  新增 IO 形态必须同步进清单（复核时优先看清单这一处）。
- 两道闸的失败信息均带文件与行号，可直接定位。

## Testing

- `verify-host-command-ids.py --self-test` 6/6 绿；`verify-core-purity.py --self-test` 4/4 绿。
- 变异实测：壳侧改回字面量 → 判红；插件侧造未知命令 → 判红；向 Core 塞入 `File.Exists` → 判红并指出行号。
- 全仓实跑两道闸均绿；CI 与 pre-commit 执行点已接线（`ci.yml` docs job + `.githooks/pre-commit`）。

## Related

- [host-command-ids](../architecture/2026-10-20-host-command-ids.md)——命令 ID 契约本身（单一事实源与 ID 集合）。
- [core-purity-boundaries](../architecture/2026-10-20-core-purity-boundaries.md)——Core 纯度的判定内容与边界。
