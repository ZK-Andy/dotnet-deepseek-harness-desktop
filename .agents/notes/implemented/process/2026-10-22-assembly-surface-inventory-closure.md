# Agent Note: 装配面在册面闭包（根目录纳入 + 装配面声明存在性）

Status: implemented
Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

## Problem

`verify-compose-root.py` 的 C8 在册面覆盖域目录，三个分类面却仍没有判据，文件可以从那里无声溜走：

- **装配面声明可以消失**：`ASSEMBLY_SURFACE_EXTRA` 声明的三个文件（`Bootstrap/PreBuildRegistration.cs`、`Bootstrap/ShellResources.cs`、`RynOptionsProfile.cs`）由 `_assembly_files` 收进 C1/C2 方向闸，而它取文件的方式是 `if path.is_file()`——文件不在了就静默跳过，门禁对该文件整体失明且无任何输出。删掉 `RynOptionsProfile.cs`，C1/C2 只会少扫一个文件，剩下的根集照旧通过。
- **壳工程根目录不在任何在册面**：根级 `.cs` 只要不叫 `Program.cs`/`DesktopAdapter*.cs`，就不属根集、不属 `ASSEMBLY_SURFACE_EXTRA`、不在域目录清单里——三张网都漏。活证据是壳工程根目录的 `WebkitSandboxFallback.cs`：它既不受 C1/C2 约束，也不在 `scripts/domain-files.manifest.json` 里。
- **装配面文件的 dot 分部会逸出方向闸**：`ASSEMBLY_SURFACE_EXTRA` 按精确路径声明，`_assembly_files` 也只 join 精确路径，而 C7 按 glob `PreBuildRegistration*.cs` 收构造。同一族两种形状：把 `PreBuildRegistration.cs` 拆成 `PreBuildRegistration.Extra.cs`，分部落在 C7 覆盖里，却逸出 C1/C2 的方法清单封闭——「搬出根不得脱闸」要的正是这种形态被拦住。

前两条是上一批 ADR（域文件在册闸泛化，[domain-file-inventory-gate](2026-10-22-domain-file-inventory-gate.md)）在 Consequences 里明写的待清偿项；第三条是 R2 评审查出的同族形状裂缝。

## Decision

C8 现在是「文件在册」判据，四条规则同属一个事实：每个 `.cs` 文件的身份必须与现实一致。

1. **装配面声明须存在**：`ASSEMBLY_SURFACE_EXTRA` 的每条路径必须在 `--src` 下存在，否则判红（`_assembly_files` 的取文件方式不动，另立判据判红）。
2. **根级文件只有两种身份**：壳工程根目录下的 `.cs` 须是根集（`Program.cs`/`DesktopAdapter*.cs`）或 `ASSEMBLY_SURFACE_EXTRA` 声明的装配面文件；两者皆非即判红。根目录是装配面区，不是域目录，因此它不进域清单，而是由判据直接管住。根集的认定按文件名（任意深度皆算根集），判据 2 只扫壳工程根目录这一层——两层判据各有各的面，名字只有一处定义（`scripts/gate_common.py` 的 `is_compose_root_name`，本闸与 F3 尺寸闸共用）。
3. **一个文件一个家**：同时登记在域清单与 `ASSEMBLY_SURFACE_EXTRA` 的文件判红（避免两处登记互相掩护，重演上一批 R1 指出的双登记盲区）；域清单里登记了根集名的文件同样判红——根集按名认定，登记它等于给同一文件两个主人（方向闸与在册面）。
4. **装配面文件的分部留在方向闸内**：`_assembly_files` 按声明文件的 stem 家族展开（`PreBuildRegistration.cs` 的家族含 `PreBuildRegistration*.cs`），分部因此仍进 C1/C2，其装配成员须在自己的 `ALLOWED_METHODS` 条目下被显式许可——分部要进闸就得改闸，形状统一。

配套归位：`WebkitSandboxFallback.cs` 搬进 `Bootstrap/` 域（命名空间 `DeepSeek.Harness.Desktop.Bootstrap`；csproj 全局 using 已覆盖该域，调用点 `DesktopAdapter.cs:26` 与记序测试 `CompositionRootSequenceTests.cs:23` 的文本零改动），并在域清单 `Bootstrap` 键登记（12 → 13，全库 49 → 50 项）。

实现落点：`scripts/verify-compose-root.py` 新增 `_assembly_surface_violations()`（判据 1、2）与 `_assembly_identity()`（装配面身份表：`ASSEMBLY_SURFACE_EXTRA` 按目录分组 + 根级装配面文件名，声明面只分解一次），`_domain_inventory_violations()` 增加双登记判据与根集轴查重（判据 3），`_assembly_files()` 展开 stem 家族（判据 4）；根集判定抽到 `scripts/gate_common.py` 的 `is_compose_root_name()`，本闸与 `scripts/verify-code-health.py` 的 F3 尺寸闸共用同一份定义（此前两份逐字相同的副本，改名即漂移）；`--self-test` 补 7 条夹具（双登记、根集名登记、游离根级文件、消失的装配面文件、根集与装配面并存合规、装配面分部落进方向闸范围、夹具断言命中行数唯一）。

## Alternatives considered

- **清单增设根目录键「.」登记根级文件**（保留 `WebkitSandboxFallback.cs` 原地）：落败原因——根目录属装配面区，不是域目录，把它登进域清单只解得了判据 2，文件仍然不受 C1/C2 的方法清单封闭（那才是它今天真正缺的约束），等于用登记表换掉方向闸；本仓库的方向是域文件归域（架构标准 R1）。
- **把 `WebkitSandboxFallback` 登记进 `ASSEMBLY_SURFACE_EXTRA`**：落败原因——它含真实逻辑（读 sysctl、设环境变量、loud 日志），进 C1/C2 会与「装配面只装配」正面冲突；C2 的方法清单封闭也会逼它拆方法。
- **只闭合「装配面声明须存在」，根目录另开一批**：落败原因——根目录零清单是今天真实存在的逃逸面（`WebkitSandboxFallback.cs` 现存），不封等于把已知逃逸留到下一批，而它只需搬一个文件。
- **禁掉根级装配面文件（根目录只许根集）**：落败原因——`RynOptionsProfile.cs` 是合法的根级装配面文件（`RynOptionsProfile.Apply`），禁掉它需要另一处安放（它也可同法搬进 `Bootstrap/`，但那是另一批的重构）。本批保留根级装配面这一身份，代价是判据 2 要多认一种身份。
- **先问逃逸对象该不该以现状存在**：`WebkitSandboxFallback` 读的是平台 sysctl，形态上像平台探测（下沉 Infrastructure 更合适）【推断 · 未证】。本批不评估该重构——它牵动 Core/Platform 的依赖方向与既有 ADR 的分层决定，不是门禁闭合批的范围；本批只保证它此后不再逃逸在册面。
- **新设 C9 检查号**：落败原因——三条规则同属「文件分类必须与现实一致」一件事，拆成两个编号会让脚本 docstring 与架构标准文档的检查表再长一格，而 `docs/architecture-standards.md` 词数余量只有 1 词。
- **只加评审检查项不进门禁**：落败原因——这几条都是机器可判的文件事实（存在性、身份、覆盖范围），AGENTS.md「新规范默认问能不能进 verify/analyzer」要求机器化。
- **判据 4 的两个落败形状**：一是把 `ASSEMBLY_SURFACE_EXTRA` 改成 glob 声明（`Bootstrap/PreBuildRegistration*.cs`）——声明面与判据面混成一处，且 `ALLOWED_METHODS` 仍按精确路径键，分部照样要改闸，只是把「一处一个家」变成「两处一个家」；二是接受分部逸出 C1/C2、只靠 C7 的 glob 兜住——那正是「搬出根不得脱闸」要否掉的形态。选家族展开：声明面不动，方向闸覆盖面按族补齐。

## Consequences

- **判据 2 会把未来的根级文件判红**：新增根级 `.cs` 必须先归位到域目录或登记为装配面。这是刻意的：根目录今天有四个 `.cs`，只有两种合法身份（根集 ×3、装配面 ×1），第三种身份没有位置。
- **判据 3 要求同步改两处**：登记一个装配面文件时，脚本与清单只能选一处为家；双登记会被判红，代价是登记动作多一步判断。
- **判据 4 扩大了 C1/C2 的覆盖面**：装配面文件的分部今后要进 `ALLOWED_METHODS` 才能过 C2。今天仓库里没有这种分部，判据对现状零影响；它挡的是下一次拆分——拆分从此要改闸（FULL），不再是静默逸出。
- **本批不含架构图**：`docs/architecture.diagram.json` 及其产物的「组合根」旧词是另一笔债，继续记账（见 [assembly-surface-terminology](2026-10-22-assembly-surface-terminology.md) 的 Consequences）。图源自身另有两处缺陷——JSON 缺 `meta.output` 字段、且 `sys` 组件落在「桌面进程」boundary 框内却不在其 `wraps` 里（archify 2.17.0 校验实测报 `meta.output must be a portable POSIX-relative path` 与 `layout/boundary-encloses-non-member`），故重新生成不是改词重跑，而是修图源结构加 schema 迁移。
- **验收读数**：`python3 scripts/verify-compose-root.py --enforce` 输出 `C8 inventory: 50 files in 5 domain dirs` 且 `compose-root: OK`；`--self-test` 全绿，新增 7 条夹具各自 `ok:`（双登记、根集名登记、游离根级文件、消失的装配面文件、合规根目录、装配面分部落进方向闸范围、夹具断言命中行数唯一）；根目录只剩 `Program.cs`/`DesktopAdapter*.cs`/`RynOptionsProfile.cs`，任意新增一个根级 `.cs` 即判红；删掉任一 `ASSEMBLY_SURFACE_EXTRA` 文件即判红，消息含 `declared in ASSEMBLY_SURFACE_EXTRA but the file does not exist`；`dotnet build` 0 警告 0 错误，`dotnet test` 1093/1093（`scripts/test-baseline.json` 基线不变）。
- **判据改动**：`verify-compose-root.py` 属 `gate-criteria` FULL 触发路径，本批按 FULL 走 R1/R2/R3 三审，证据行在本篇头区。

## Related

- [domain-file-inventory-gate](2026-10-22-domain-file-inventory-gate.md)——本篇清偿的待清偿项出处。
- [assembly-surface-terminology](2026-10-22-assembly-surface-terminology.md)——同批术语退役。
