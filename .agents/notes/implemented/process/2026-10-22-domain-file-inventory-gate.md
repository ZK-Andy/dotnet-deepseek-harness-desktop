# Agent Note: 域文件在册闸泛化并双向校验

Status: implemented
Erratum: 2026-10-22 — 「装配面文件可登可不登」的口径已被收紧为「只能登一处」：域清单与 `ASSEMBLY_SURFACE_EXTRA` 双登记判红，域清单登记根集名的文件同样判红（[assembly-surface-inventory-closure](2026-10-22-assembly-surface-inventory-closure.md)）。其余决定与读数不变。
Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

## Problem

C8（域文件须在册）原来只覆盖壳工程的一个目录：`scripts/verify-compose-root.py` 里的 `BOOTSTRAP_DOMAIN_FILES` 列出 `Bootstrap/` 下 12 个非装配面文件；`ASSEMBLY_SURFACE_EXTRA` 的 3 个装配面文件另有归属（2 个在 `Bootstrap/`、1 个在壳工程根），本就不入域文件面。缺口三处：

1. **覆盖面**：壳工程有五个域目录——`Bootstrap/`（14 个 `.cs`）、`PageBridge/`（19）、`Recovery/`（7）、`Tray/`（8）、`Update/`（3）；C8 只认第一个。父批 [shell-router-rehoming](../architecture/2026-10-21-shell-router-rehoming.md) 的意图是「搬进域目录不得脱闸」，但把四个页面桥路由迁入 `PageBridge/` 时，那条意图在 `PageBridge/` 没有机器实现（该 ADR 挂账 ②）。
2. **方向**：原实现只做 `actual - declared`，即「目录里有未在册文件」判红；反向的 `declared - actual` 无人查。文件改名或删除后，清单留一条永久死项，静默漂移直到有人重新审阅清单（挂账 ③）。C7 早已对 `PREBUILD_CONSTRUCTIBLE` 做了对称死项校验，C8 缺同一半。
3. **判据与数据的税混同**：清单内联在 `verify-*.py` 里，则「域目录新增一个文件」等于改门禁脚本，`scripts/verify-review-tier.py` 的 `gate-criteria` 触发器（路径名以 `verify-` 开头且后缀 `.py`/`.sh`）会把它判成 FULL 档，例行加文件因此背上一次 R1/R2/R3 三审。C8 的语义是**分类声明**（这个文件被分类过），不是**判据变更**（哪些文件算装配面）；用判据的流程税为声明埋单价不对等。

严重级定性：门禁覆盖面与判据形态缺陷。不改产品行为、不改 wire，失守时的后果是「装配代码迁入域目录后脱离 C1/C2/C7 的扫描面」无人报警。

## Decision

C8 的清单从判据文件里搬出来，判据泛化为「所有域目录 + 双向」。

- **数据面**：`scripts/domain-files.manifest.json`，键 = 域目录名（相对 `--src` 的一级子目录），值 = 该目录下的 `.cs` 文件名列表；`_` 前缀键写说明，加载时忽略。当前内容：`Bootstrap` 13 项（该目录 15 个 `.cs` 中除两个装配面文件外的全部）、`PageBridge` 19、`Recovery` 7、`Tray` 8、`Update` 3，合计 50 项。
- **加载**：`_load_domain_manifest(path)` 读 JSON；文件不可读、非法 JSON、顶层不是对象、条目不是字符串列表一律 `SystemExit`（fail loud）。空清单不降级为「没有域文件」。
- **判据**：`_domain_inventory_violations(src, manifest)` 扫 `--src` 下每个一级子目录（`bin`/`obj` 排除），四条行文：
  - `C8 <dir>/<file>: new file is neither domain nor assembly surface (...)`——目录在册但文件未在册；
  - `C8 <dir>/: domain directory is not in the manifest (...)`——含 `.cs` 的目录完全未登记；
  - `C8 <dir>/<file>: inventory lists a file that does not exist (dead entry — ...)`——在册而文件已消失；
  - `C8 <dir>/: inventory lists a directory that does not exist (dead entry — drop it)`——在册目录已消失。
- **装配面判据不动**：`ASSEMBLY_SURFACE_EXTRA`（3 项）留在脚本内，视为「判据」；其文件免于 manifest 在册（可登可不登，登了也不进死项方向——装配面路径由 C1/C2 自行锚定）。改它仍是 FULL。
- **死项与空目录**：一个目录仍在、`.cs` 已清零时，其清单项照旧进死项方向判红——「目录没了」不是忘掉清单的理由。
- **入口**：`main()` 加载清单（`--domain-manifest` 可覆盖），把 C8 行追加到其它检查之后，并打印 `C8 inventory: <N> files in <M> domain dirs`。C8 不挂在 `_violations()` 里——该函数的自测夹具（临时 `Shell` 工程）不该被仓库的真实清单约束。
- **自测**：`_self_test()` 增 11 条夹具——合规章、装配面文件免在册、未在册文件、未登记目录、无 `.cs` 目录被忽略、死文件项、死目录项、`.cs` 清零的目录不藏死项、坏 JSON、顶层非对象、条目非字符串列表。

## Alternatives considered

- **清单继续内联在 `verify-compose-root.py`**（落败）：判据变更与声明数据共用同一份文件，于是每个新增域文件都触发 FULL 三审（`gate-criteria` 触发器按路径名判定，不分辨改动是判据还是数据）。数据/判据分离在本仓已有两份先例——`scripts/doc-budgets.manifest.json`（字数预算数额）与 `scripts/test-baseline.json`（测试基线数）。
- **只泛化目录面、保留单向检查**（落败）：只查 `actual - declared` 时改名/删除依旧静默漂移，正是挂账 ③ 原文；C7 的对称先例也证伪了「单向足够」。
- **把 `ASSEMBLY_SURFACE_EXTRA` 一并搬进 JSON**（落败）：装配面归属是判据（决定 C1/C2 扫谁），搬进数据面等于把「新增装配面文件 = FULL」这条防线降级为一次数据编辑。
- **用结构约定替代清单（如「每个域目录必须有 `*Registration.cs`」）**（落败）：约定只断言某类文件存在，断言不了「每个 `.cs` 都被分类过」；`Bootstrap/` 是域文件与装配面混居、`PageBridge/` 是纯域目录，唯有同一形态的逐文件清单能统一表达两种现实。
- **保留 C8 单目录语义，为 `PageBridge/` 另立 C9**（落败）：同一不变量两份实现、两套消息与自测，且新开第六个域目录时还要再写 C10；泛化后一条规则随目录自动生长。

## Consequences

- 买到：C8 从「一个目录 + 单向」变成「所有域目录 + 双向 + 数据面清单」，挂账 ②③ 同时清；新增域文件的例行税降到改数据文件（LIGHT），而装配面归属仍是判据（FULL）。
- 付出：多一份需要维护的清单文件（50 行数据）。清单可能被「照抄实际文件」而失去分类判断力——机器只保证「没有人静默新增或改名」，分类正确性留评审（R1/R3 检查项）。
- 实测读数：`verify-compose-root.py --enforce` → `root new-of-owned count: 3 (cap 6)`、`C7 inventory: 24 entries`、`C8 inventory: 49 files in 5 domain dirs`、OK；`--self-test` 全绿（11 条 C8 夹具）。
- 判据改动：`verify-compose-root.py` 属 `gate-criteria` FULL 触发路径，本批按 FULL 走 R1/R2/R3 三审，证据行写本笔记头区。
- 已继承的债：历史 ADR [shell-router-rehoming](../architecture/2026-10-21-shell-router-rehoming.md):31 引用了已消失的常量名 `BOOTSTRAP_DOMAIN_FILES`，按证据严肃性规则在该篇 Status 下补 `Erratum:` 行（正文与决定不动）。该篇另因同批删除 `ShellCommandRegistration.cs` 而补的第二句在同一条 Erratum 里。
- 待清偿项（本批不做）：`_assembly_files` 对 `ASSEMBLY_SURFACE_EXTRA` 里已消失的文件仍是 `if path.is_file()` 静默跳过；壳工程根目录（`Program.cs`/`DesktopAdapter*` 之外的 `.cs`）仍是零清单——根集新增文件不脱闸，但非根集根文件既不在 C1/C2/C5 也不在 C8。两条都在同一面，要闭合须另立判据（FULL + 自带 ADR）。

## Related

- [per-domain-registration-files](../architecture/2026-10-22-per-domain-registration-files.md)（implemented）：清挂账 ①（注册文件按域下沉），本批清 ②③。
- [assembly-surface-terminology](2026-10-22-assembly-surface-terminology.md)（implemented）：同批的治理词汇批次，C8 相关措辞随其定名。
- [shell-router-rehoming](../architecture/2026-10-21-shell-router-rehoming.md)（implemented）：挂账 ②③ 的出处；常量名失效以 Erratum 行记录。
- [assembly-surface-inventory-closure](2026-10-22-assembly-surface-inventory-closure.md)（implemented）：闭合本篇「待清偿项」的两条（装配面声明须存在、壳工程根目录纳入在册面），并把「装配面文件可登可不登」收紧为一个文件一个家。
