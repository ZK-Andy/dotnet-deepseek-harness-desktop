# Agent Note: VSTHRD 门禁覆盖测试工程

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

三审结论：R1/R2 0 Blocker。采纳——PayloadSmoke 一并挂分析器（同一规则不该两种强度，同批 build 仍 0 警告）、豁免断言改实测口径（156 个带 `[Fact]`/`[Theory]` 的 awaitable 方法全无 `Async` 尾缀且零告警）；拒绝——把测试内假件抽到共享测试库（各有独立捕获面，结构代价远超收益）。R3 0 Blocker。采纳——补 `## Related` 指向 `review-to-machine-gates` 并同批改写其「主工程加 analyzer」的范围表述、测试计数改 854。

## Problem

D001（`Async` 尾缀 = VSTHRD200）/ D002（`async void` = VSTHRD100）此前只在三个 src 工程生效——`Microsoft.VisualStudio.Threading.Analyzers` 仅挂在 src（`PrivateAssets="all"`），三个 xunit 测试工程不引用。测试侧的 awaitable 命名因此只靠人眼守，且没人知道真实缺口大小：2026-09-27 的 R2 评审在 `f5e215c` 批上提出该缺口时，描述为「约 20 个既有测试名无 `Async` 尾缀」，但候选是从带 `[Fact]` 的测试方法粗筛出来的，与实际会被分析器拦下的集合不是一回事。

## Decision

- 三个 xunit 测试工程（Core.Tests / Infrastructure.Tests / Tests）与 src 同源挂 `Microsoft.VisualStudio.Threading.Analyzers`（`PrivateAssets="all"`），build 门禁对测试代码按同样强度报 VSTHRD。PayloadSmoke 工程同批挂上（它在 slnx 内、受 CI 的 `dotnet format` warn 级校验覆盖，补齐成本为零；原计划的「非 xunit 工程故豁免」在评审后收口时推翻——同一条规则不该有两种强制强度）。
- 实测（首次挂上即实测）：该分析器在测试工程只报 VSTHRD200 一类、16 处，**全部是测试内假件/辅助成员**（`RunFake` ×10、`ProbeOk` ×1、`ProbeDead` ×2、`Fake` ×1、`Route` ×2），无一是 `[Fact]` 测试名。豁免不是推断而是量出来的：三个测试工程带 `[Fact]`/`[Theory]` 特性的 awaitable 方法共 **156 个、无一以 `Async` 结尾**，零告警；与同批 16 个假件成员被逐一点名形成对照。因此此前「约 20 个测试名需改名」的判断不成立——改名面是 16 个私有辅助成员，零测试名变更。
- 这 16 处一律补 `Async` 尾缀（`RunFakeAsync` / `ProbeOkAsync` / `ProbeDeadAsync` / `FakeAsync` / `RouteAsync`），声明与调用点同批原子改；全部是文件内自用标识符，无跨文件引用。

## Alternatives considered

- **维持评审兜底**：落败——机器可查的不变量该进门禁（根 AGENTS.md「新规范默认问能不能进 verify/analyzer」）。D001/D002 已在 src 机器化，测试工程留口子等于同一条规则两套强制强度。
- **在测试工程关掉 VSTHRD200（`.editorconfig` `severity = none`）**：落败——关规则是消音不是覆盖，测试侧新代码的同类漂移再无人拦。
- **对 16 处逐点 `#pragma` / `SuppressMessage`**：落败——16 处抑制的维护面大于一次改名，且抑制会把新代码的同类问题一并盖住。
- **把测试内假件抽到共享测试库统一命名**：落败——这批假件刻意就近定义在用例内（各自捕获不同的 `ProcessStartInfo`/日志列表），为它们引入跨工程测试库是结构代价远超收益。
- **PayloadSmoke 保持豁免**：落败——它虽不是 xunit 工程，却与测试工程同处一个 slnx、同受 format 门禁覆盖；留着等于让「同一规则、同一条流水线」出现两种强度。

## Consequences

测试侧的 awaitable 命名与 `async void` 从此与 src 同档强制：build 0 警告即是门禁。代价：四个测试/冒烟工程各多一个编译期分析器（开销可忽略）；新增 awaitable 测试假件须带 `Async` 尾缀。测试名命名风格不受影响（豁免保留，已实测）。

## Related

- [review-to-machine-gates](2026-09-13-review-to-machine-gates.md)：本批是它「D001/D002 机器化」的范围延伸（从主工程扩到测试/冒烟工程）。

## Testing

- `dotnet build --no-incremental`：挂分析器后实测 16 处 VSTHRD200，改名后 **0 警告 0 错误**（含同批挂上分析器的 PayloadSmoke）。
- `dotnet test`：**854/854**（166+182+506）绿。
