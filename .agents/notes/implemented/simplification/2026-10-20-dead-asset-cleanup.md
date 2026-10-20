# Agent Note: 死物清理与 hooks 显式化

Status: implemented

Review: FULL/2026-10-20/R1=ok R2=ok R3=ok

## Problem

仓库里留着三份**全仓零引用**的入库资产，外加一个零读者的常量：

- `assets/desktop.patch.yml`——内容是「预装插件市场 dshmarket」，与「壳不代装市场」的既定立场（ADR `2026-10-17-cancel-firstboot-market-preinstall`）直接冲突，且全仓无人读取。
- `templates/agents-hierarchy.md`——同目录另两个模板（`adr-proposed.md`/`adr-implemented.md`）被 `.agents/notes/README.md` 引用而它是活的；本文件在仓库内不可达。
- `scripts/setup-hooks.sh`——全仓唯一设置 `core.hooksPath=.githooks` 的入口，但 `docs/development.md` 没有任何指引去跑它：新克隆按文档走，hooks 静默失效（提交不跑快检查）。
- `DesktopUpdateCommandRouter.Prefix`（`"desktop.update."`）——全仓零读者，命令名在 `CanRoute` 里硬编码，删它不改变任何行为。

前三份是「承诺了却没人用」的死物：留着不是零成本——它们会被人读成「这份资产有人用」，而 `desktop.patch.yml` 还会把已经废止的市场预装立场读回来。

## Decision

- 删 `assets/desktop.patch.yml`、`templates/agents-hierarchy.md`、`scripts/setup-hooks.sh`（`git rm`）。
- 删 `DesktopUpdateCommandRouter.Prefix` 常量。
- hooks 配置改为**文档里的一步**：docs/development.md 新增「首次克隆（git hooks）」节，写明 `git config core.hooksPath .githooks`，并说明不设则快检查静默失效（CI 仍是穷尽矩阵）。

## Alternatives considered

- **维持现状不修**（三份文件与常量都留着，hooks 继续靠脚本）：落败——留着不是零成本：读者会把不可达文件读成「有人用」，`desktop.patch.yml` 还会把已废止的市场预装立场读回来；hooks 那条则是新克隆按文档走就静默失去快检查，属于「文档没说清导致的失效」，代价落在每个后来人身上。
- **保留 `setup-hooks.sh` 并在 docs 里指向它**：落败——脚本只做一行 `git config`，脚本与文档会形成两个入口、多一个「文档是否指对了脚本」的判断；一行命令直接写进文档更短。
- **保留 `templates/agents-hierarchy.md` 并给它挂入口**（`.agents/AGENTS.md` 或 notes README）：落败——本仓没有「AI 协作层级」的消费方（技能由 DSH 自动发现），挂入口等于为不存在的需求造可达性。
- **保留 `assets/desktop.patch.yml`（当示例/参考）**：落败——它的内容是已废止立场的具体化，留着只会误导；同类示例放 `docs/` 才是活的位置。
- **用 `Prefix` 反向拼出 `CanRoute` 的命令名**：落败——`Prefix` 不是命令 ID 的单一事实源；真正要收口的是 Core 侧强类型常量，由审计报告 §8 第 4 项（尚未落地）另立 ADR 处理。此处留着只是字面量双写，不如直接删。

## Consequences

- 仓库少三份死物 + 一个死常量；`docs/development.md` 有了 hooks 的唯一指引位（新克隆不再静默失去快检查）。
- `assets/desktop.patch.yml` 承载的「预装 dshmarket」配置随文件一起消失——该立场本已在 ADR `2026-10-17-cancel-firstboot-market-preinstall` 废止，无实现依赖。该文件名今天仍会出现在一处历史叙述里（`bug-fix/2026-08-20-dshmarket-background-install` 的 Problem 段记的是 0.1.8 那次事故），那是当时的事实记录，按本仓纪律不插勘误；资产本身已删除。
- 命令 ID 的收口不在本篇：跨包命令名仍散在壳侧与 companion 侧，由审计报告 §8 第 4 项（尚未落地）另立 ADR 收口。

## Testing

- 三份文件的删除用全仓引用扫描确认（删除前逐个 grep：命中数 0，仅 `setup-hooks.sh` 自身文件头出现）。
- `DesktopUpdateCommandRouter.Prefix` 删除后全仓无 `.Prefix` 引用残留；`dotnet build` + `dotnet test` 全绿（1050/1050）。
- 文档门禁全绿：`verify-doc-budgets`（含本篇新增的 docs/development.md 节）、`verify-md-links`、`verify-readme-badges`。

## Related

- [cancel-firstboot-market-preinstall](../architecture/2026-10-17-cancel-firstboot-market-preinstall.md)——`assets/desktop.patch.yml` 的内容与该篇废止的立场冲突。
- [dshmarket-background-install](../bug-fix/2026-08-20-dshmarket-background-install.md)——该文件名在 0.1.8 事故记述里的历史残留（资产已删，记述不改）。
