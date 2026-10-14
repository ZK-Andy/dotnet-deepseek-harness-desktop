# Agent Note: 启动用例下沉 Core——进入主界面与运行时监督

Status: implemented

Review: FULL/2026-10-15#2/R1=ok R2=ok R3=ok

方案母篇见 [composition-mechanism-container-assembly](../../proposed/architecture/2026-10-08-composition-mechanism-container-assembly.md)（本篇落其 step-1 的 B1/B2；四步更正见 [desktop-adapter-rename-final-assembly](2026-10-08-desktop-adapter-rename-final-assembly.md) Erratum）。

## Problem

母篇「用例下沉 Core」只落了 `IWindowChromePolicy` 与 `IRuntimeStarter` 两件（`1b83ccf`、`9bfc95d`），导航与监督两件未落：`StartupSequence` 仍是流亡的组合根——构造器收具体类型、`Run` 链同步阻塞、`SetupSupervisor` 在编排面 `new RuntimeSupervisor`、失败面与恢复回调（闩语义、模态不叠、回落面补导航）全挤在编排分部里。后果是可测面缺口：进入主界面链（铸币→等窗→探针裁决→鉴权重载）与恢复链（失败面→重启→重验→收口导航）只能靠真实窗口/进程驱动，无法经 fake 覆盖；而它们恰是启动最敏感的链（spawn→铸币→导航）。

## Decision

- **进入主界面用例下沉 Core**（`Core.Ui.MainUiEntry` + 时限记录 `MainUiEntryOptions`）：原 `EnterMainUi`/`SettleWebSession` 与导航原语（`Task.Run` 隔离同步 `set_url`、有界调用、提交等待、探针重试）逐句搬迁，探针脚本与时限为构造数据，日志串不变。
- **新 Core 端口 `IWebViewNavigator`**（窗口就绪探测 / 导航 / 到达信号订阅 / 脚本求值）：Ryn 边界留在 Presentation 适配器 `PageBridge.WebViewNavigator`（桥回报 JSON 外壳的还原 `RynProbeValue` 属实现侧，端口返回解码后文本）。
- **端口实现落点的成文例外**：三件新端口的适配器全在壳工程 Presentation——`ArchitectureTests.InfrastructureAssembly_HasZeroPresentationReferences` 断言 Infrastructure 引 Ryn 即违 R2，故触 Ryn/UI 的适配器只能住 Presentation（`architecture-standards` R3「实现进 Infrastructure」的既有例外，判例见 [value-flow-batch1](../../implemented/architecture/2026-09-15-value-flow-batch1-state-sinking.md)）；`docs/architecture.md` 已按此口径改写。用例侧另有第四处边界：铸币 `Func<DshWebUrl, Action<string>, CancellationToken, Task<bool>>` 委托注入（沿用 `RuntimeStarter` 先例，未新造端口）。
- **监督用例下沉 Core**（`Core.Bootstrap.RuntimeSupervision` + `RuntimeSupervisionOptions`）：引导落定门控、`RuntimeSupervisor` 循环、失败面闩（一周期一次、模态不叠）、恢复回调重验与回落面收口导航整体搬迁；监督器由用例内部持有，不再由编排面 `new`。
- **新 Core 端口 `IRecoverySurface`**（原生通道可用性 / 原生展示 / 页内回落展示）与 **`IMintEpoch`**（失效 / 收养重验）：适配器 `Recovery.RecoverySurface`（正文材料、诊断包、语言分支、退出动作）与 `Bootstrap.ShellMintEpochAdapter`（转发 `DshShellForward`）。失败原因选择仍是用例侧域决策（`UiCopy.RecoveryReason`），承载面只消费。
- **`StartupSequence` 只留接线**：删 `StartupSequence.Navigation.cs`/`StartupSequence.WebSession.cs` 两分部与四个共享字段（代理/转发器/失败面两端口），`RunBootstrapIfNeeded` 与 `SetupSupervisor` 改为容器解析用例并调用；用例与适配器在 `*Registration.cs` 组装（R1）。
- **三处记名的形状变化**（行为等价，逐条列出以便复核）：① 恢复回调的铸币重验由 `GetAwaiter().GetResult()` 改 `await`——该回调运行在监督任务线程（`Task.Run` 起，无同步上下文），阻塞等 I/O 无收益，行为序（重验完成 → 解闩 → 条件收口导航）不变；② 收口导航改经端口，原生同步段随 `WebViewNavigator` 的 `Task.Run` 隔离进池线程（原件在监督线程直呼；两条路径都在 `onRecovered` 上 await，能观察到的差异只有执行线程）；③ 失败面模态的取消面由实现里写死 `CancellationToken.None` 变为端口契约 `ct`（当前唯一调用方仍传 `None`）。

## Alternatives considered

- **整段搬去 Presentation 适配器（不落 Core）**：能满足「排序器减重」，但母篇的判据是「用例住 Core、可单测」；留 Presentation 则该链仍不可单测，等于只做搬家不做拆解。
- **端口只提供有界导航（时限/提交等待留适配器）**：适配器要自己读配置、自己记日志，时序与重试决策散到边界层且不可单测——与「决策在用例、边界只搬运」相反。
- **保留同步阻塞重验**：阻塞监督线程等 HTTP 稳定化（最长 30s 预算）只为省一个 `await`；监督线程是池线程，`await` 在此无死锁面，保留阻塞纯属遗留形状。
- **端口实现落 Infrastructure（R3 字面口径）**：`DshShellForward` 在 Infrastructure，但导航/失败面适配要触 Ryn 类型（`CurrentWindowAccessor`/`IRynWindow`/`RynNavigationCallbacks`），而 Infrastructure 引 Ryn 违 `ArchitectureTests` 的 R2 断言；强行走 Infrastructure 只能把 Ryn 类型再包一层端口，收益为负。落 Presentation 是本仓既有判例（[value-flow-batch1](../../implemented/architecture/2026-09-15-value-flow-batch1-state-sinking.md)），故按例外成文而非改口径。
- **把页面健康观测/顶栏注入/启动期告知并进监督用例**：它们不是监督语义（宿主进程存活与窗口承载无关），并进去即造第二个上帝对象，撞 R1 评审面。
- **失败面闩与承载决策留在适配器（用例只转发）**：闩语义（一周期一次、不叠模态、回落面补收口导航）是恢复策略，留在适配器则监督用例只剩胶水，恢复链仍不可测。

## Consequences

- 买到的：进入主界面链与恢复链可经 fake 端口单测（新增 11 例）；`StartupSequence` 少两个分部与四个字段、少一处 `new` 具体类型；导航/失败面/铸币态三处 Ryn/基础设施边界端口化（R3）；`MainUiEntry` 不再依赖 `RynNavigationCallbacks` 单槽的调用时序说明（订阅语义收进端口）。
- 付出的：新增三端口 + 三适配器 + 两用例（`Core.Ui` 新目录）；时限参数经 `MainUiEntryOptions`/`RuntimeSupervisionOptions` 从 `RuntimeTimeouts` 快照（配置键仍是唯一家，记录不持默认值）；`DesktopAdapter.App.cs`/`DesktopAdapter.cs` 各一行接线变更（该构造函数不再收 proxy）。
- 剩余：母篇 step-1 的 **B3**（`Run` 缩成排序器：`SetupCaptionBar` 脚本构建移 Presentation 用例、构造参数改 DI）与 **B4**（`verify-compose-root.py` 新增 C6「排序器形状」+ 母篇转 implemented）留后续批次；`Run()` 的唯一入口桥 `.GetAwaiter().GetResult()` 按母篇口径保留，全 async 另立条目。

## Testing

- `dotnet test` 全绿 0 警告：229 + 225 + 6 + 603 = **1063**（+13：`MainUiEntryTests` 七例——无代理源跳过、健康页不导航、鉴权页重铸重载一次、探针耗尽 unknown、等窗超时、退出取消上抛、导航调用与提交等待双超时降级；`RuntimeSupervisionTests` 六例——落定门控短路、原生面失效+失败面+重验且零导航、残留锁死跳过重启、回落面恢复后收口导航、原生承载故障回落页内面、模态在飞不叠第二个）。
- 机器人门禁 `--enforce` 全绿：`verify-code-health`（F1–F4）、`verify-compose-root`（C3 16/16、C4/C5 零行）、`verify-registration-discipline`（R1a 1、R1b 11）、`verify-code-conventions`。
- 增量纪律闸：整批 gross 1831（`git diff <base> --cached --numstat` 合计）超 600 帽——按帽拆成四个提交（Core 用例 537 / 适配器与注册 248 / 测试与徽章同步 547 / 排序器切换 + 删两分部 + 文档与 ADR 499），每个提交的 `--staged` 读数都在帽内；既有文件净增 +3（`DesktopAdapter.App.cs`）、跨层扇出 0、新包引用 0。
- 源锚回归随新家同步：`CompositionRootSequenceTests.RuntimeSupervision_RecoveryStart_InvalidatesMintRoute` 改锚 `Core/Bootstrap/RuntimeSupervision.cs` 的 `_mintEpoch.Invalidate()`。
- 覆盖率徽章维持（`scripts/test-baseline.json` 只同步测试数 1063/1063）。
- **实机未验**：本批搬动的是最深敏感链（spawn→铸币→导航）与恢复链，按母篇要求须一次全链实机（冷启动 + 引导 + 收养恢复）；该面不在本机可执行范围，与波 2 同例挂待办，由实机批次销账。
