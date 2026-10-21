# Agent Note: 装配面术语退役组合根

Status: implemented
Erratum: 2026-10-22 — 本篇 Consequences 的图挂账已关闭：`docs/architecture.diagram.json` 与两个产物 `docs/architecture.html`/`assets/architecture.svg` 已删除（见 [retire-architecture-diagram](../simplification/2026-10-22-retire-architecture-diagram.md)）；正文不动。本篇保留术语退役决策。

Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

## Problem

「组合根」（composition root）在本仓库同时承担两件事：命名 `Program.cs` + `DesktopAdapter*` 这一层**只装配的面**，以及暗示一个**集中承载一切的根**。前者是仍然成立的结构事实，后者正是 [compose-root-form-separation](../architecture/2026-09-28-compose-root-form-separation.md) 之后被逐批拆掉的东西——编排归 `Bootstrap/StartupSequence`、Build 前构造归 `Bootstrap/PreBuildRegistration`、注册按域下沉（[per-domain-registration-files](../architecture/2026-10-22-per-domain-registration-files.md)）。今天壳根只剩 `RegisterServices` 的平铺调用清单，但词汇仍在教读者旧模型：

- 契约文档把「组合根只装配」写成 R1 条目标题（`docs/architecture-standards.md` R1、根 `AGENTS.md` 评审检查项），读者读到的是「有一个根，它只装配」——一个已经不存在的对象。
- 门禁标签、hook 注释、CI 步骤名（`scripts/verify-code-health.py` 的 F3、`scripts/verify-compose-root.py` 的语义闸、`.githooks/pre-commit`、`.github/workflows/ci.yml`）用同一旧词，机器输出与文档互证同一误解。
- 源码注释里「组合根注入」「组合根值流」「原组合根 `PathsEqual`」等表述把「面」和「根」混为一体，新读者无法判断哪些是活结构、哪些是历史叙事。

判别词：可观察行为是否变化 → 未变（纯词汇与标签）。变的是**组织模型的名字**与门禁输出，因此不入 `feature`/`bug-fix`；改变的对象是仓库治理面（契约文档、门禁标签、评审清单措辞）→ `process`。

## Decision

这一层统一叫**装配面**（assembly surface）：外壳工程里承载装配与接线的代码面，由**根集**（`src/DeepSeek.Harness.Desktop/Program.cs` + `src/DeepSeek.Harness.Desktop/DesktopAdapter*.cs`）与三个在册装配文件（`src/DeepSeek.Harness.Desktop/Bootstrap/PreBuildRegistration.cs`、`src/DeepSeek.Harness.Desktop/Bootstrap/ShellResources.cs`、`src/DeepSeek.Harness.Desktop/RynOptionsProfile.cs`）组成；规则行文用**根集**指前者，不用「装配面」代指它。

- **契约与流程文档**：`AGENTS.md`:36（架构规范一句）、`:50`（R1 条目「装配面只装配」）、`:89`（F3 说明「装配面根集合计」）、`:90`（语义闸说明：装配面=根集＋`PreBuildRegistration`/`ShellResources`/`RynOptionsProfile`，根集只装配 C1–C5）、`docs/architecture-standards.md`:23（R1 标题「装配面纪律」）/`:24`/`:27`/`:37`/`:42`、`docs/architecture.md`:18（章节「分层与装配面」）/`:20`/`:21`、`docs/testing.md`:44、`README.md`:128、`.agents/workflows/feature-flow.md`:9/`:19`、`.editorconfig`:82。
- **门禁与流水线标签**：`scripts/verify-compose-root.py`:2 docstring「assembly-surface semantics」；`scripts/verify-code-health.py`:9（`F3  assembly-surface ROOT set …`——F3 只累加根集，不累加装配面全部文件）与 F3 违规行文案；`scripts/verify-code-conventions.py`:45/`:64`（`Presentation/装配面`）、`:154` 忽略标记示例；`.githooks/pre-commit`:49（`根集只装配`）/`:78`；`.github/workflows/ci.yml`:139 步骤名。
- **源码注释**（`src/**` 与 `tests/**` 的 `.cs`，staged diff 实测 72 行）：`组合根注入`→`装配面注入`、`组合根值流`→`装配面值流`、`原组合根 <c>X</c>`→`原装配面 <c>X</c>`、`组合根容器前头部调用序`→`装配面容器前头部调用序`、`组合根源序测试`→`装配面启动序测试`等；规则性句子如 `不住组合根（R1 组合根只装配）`→`不住装配面（R1 装配面只装配）`。`.csproj` 注释不在 `.cs` 扫面，另行手改两处（Infrastructure 的「装配面注入（R3）」、Tests 的「Ryn 宿主壳/装配面测试」）。
- **保留旧词的三类**（不改）：
  1. **ADR 篇名**：「ADR 组合根机制收官」「ADR 组合根寿命与配置单一来源」是记录的名字，按证据严肃性不可改；引用它们的注释行整行保留旧词（`src/**` 中约 40 行）。
  2. **Ryn 自己的组合根**：`src/DeepSeek.Harness.Desktop/DesktopAdapter.cs`:7「Ryn 拥有真正的组合根 `RynApplication.CreateBuilder().Build()`」——指框架内部真实的 composition root，是本仓要给 Ryn 让位的那一个。
  3. **机器标识符**：脚本文件名 `scripts/verify-compose-root.py`、CLI 旗标 `--compose-total-limit`、英文 slug `compose-root-form-separation`。17 篇既有 implemented ADR 按 `.py` 文件名引用（`git grep -l 'verify-compose-root\.py' -- .agents/notes/implemented | grep -v 2026-10-22 | wc -l`），计入本批四篇共 21 篇，改名要连带一批 Erratum 且打断机器触发路径（`verify-*.py` 的 FULL 判定与 hook 调用），收益只是文件名外观——不做。
- **不做的**：不改 `docs/architecture.diagram.json` 与生成产物 `docs/architecture.html`、`assets/architecture.svg`——图由 archify `deliver` 生成（`docs/architecture.md`:11 的图源规约要求改 JSON 后重新生成），本机无 archify，手改 JSON 会让产物与图源漂移。

## Alternatives considered

- **保留「组合根」并只删掉其中的「根」语义**（落败）：词汇本身携带「有一个集中的根」的断言；文档每写一次「组合根只装配」都在重建读者心中那个已被拆掉的对象，缩写救不了句子。
- **连同判据一起删掉（F3 与 C1–C8 全撤）**（落败）：那些闸拦的是 `1434271` 式回放——阶段方法与业务判断逐片加在根上、每片靠新 dot 分部绕过单文件预算。删词可以留闸，删闸没有理由。
- **连脚本文件名与 CLI 旗标一起改**（落败）：17 篇 implemented ADR 按 `verify-compose-root.py` 引用该闸，`pre-commit`/CI 步骤与 `docs/testing.md` 表格亦按文件名调用；改名等于一批 Erratum + 全链打断，而文件名不是读者理解结构的入口。
- **把源码注释里的旧词也一律清掉（含 ADR 篇名引用行）**（落败）：引用行整句是「ADR 组合根机制收官 step-1 的 B2」这样的定位串，改名后按篇名 grep 会落空，属证据失真；保留反而让「旧词 = 历史记录」这条边界可见。
- **改 JSON 图源不重新生成**（落败）：`docs/architecture.md`:11 规定图只从 JSON 生成、不手改产物；改源不改产物正是该规约禁止的漂移，而本机无 archify 无法生成。
- **只用英文术语（assembly surface）替换中文**（落败）：本仓 durable prosa 为中文单语，`AGENTS.md`/`architecture-standards.md` 的规则句子用中文读最快；英文名已由脚本 docstring 承担。

## Consequences

- 买到：契约文档、门禁标签与源码注释说出当前结构（装配面 + 根集），不再复述已拆掉的「根」；R1 条目与 `verify-compose-root.py` 的 C1–C8 判据自洽（F3 与 C8 的措辞分别锚定「根集」「域目录」两件不同的事）。
- 付出：约 70 处注释（staged diff 72 行）+ 20 余处文档与门禁标签的一次性 diff；`docs/architecture.html`/`assets/architecture.svg` 与 `architecture.diagram.json` 三处仍写「组合根」，须在能跑 archify 的机器上改 JSON 重生成才消化（本批明确不做，作为已继承的债）。
- 已继承的债：机器标识符保留 `compose-root`（脚本名、CLI 旗标），因此「按 grep 找术语」的读者仍会在文件名与 17 篇 implemented ADR 里看到旧词——这是刻意的边界，不是遗漏。
- 判据与触发器：`scripts/verify-*.py`、`.githooks`、`ci.yml` 均在 `gate-criteria` 触发面内，本批按 FULL 走 R1/R2/R3，证据行在本笔记头区。
- 实测读数：`git grep 组合根` 在 `src/`+`tests/` 只剩三类保留项；`verify-compose-root.py`/`verify-code-health.py`/`verify-code-conventions.py` 与其 `--self-test` 全绿（标签改动不影响判据）；`verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json` 通过（CJK 连续串计 1 词，`组合根`↔`装配面` 等长替换不动预算，architecture.md 972/990、architecture-standards.md 599/600 未变）；`dotnet build` 0 警告 0 错误，`dotnet test` 237+234+6+616 = 1093/1093。

## Related

- [compose-root-form-separation](../architecture/2026-09-28-compose-root-form-separation.md)（implemented）：拆掉「上帝式根」的那批，词汇退役的对象由此产生。
- [per-domain-registration-files](../architecture/2026-10-22-per-domain-registration-files.md)（implemented）、[domain-file-inventory-gate](2026-10-22-domain-file-inventory-gate.md)（implemented）：同批清挂账 ① 与 ②③，本笔记是第三批。
- [shell-router-rehoming](../architecture/2026-10-21-shell-router-rehoming.md)（implemented）：挂账三条的出处。
