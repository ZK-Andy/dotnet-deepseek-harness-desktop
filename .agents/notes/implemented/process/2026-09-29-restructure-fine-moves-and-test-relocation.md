# Agent Note: restructure-fine-moves-and-test-relocation（余批 1：§4.1 细搬 + G 测试归位/IIV 收窄）

Status: implemented

Review: LIGHT/2026-09-29/R2=ok（首轮 0 Blocker；1 Suggestion 已采纳：测试基线家与 README 双语徽章同步 870/870，随本批收口）

中文（双语暂不启用）。

## Problem

结构整改总纲（`process/2026-09-27-post-packaging-churn-restructure`）A–F 已交付，余三块按评审档经济学重新分组（LIGHT 面归一批、FULL 触发项归一批，§4.4 安全不变量独立小批）。本批为余批 1，覆盖：

- **§4.1 细搬（编排服务内面）**：D1 把启动编排整体搬出组合根后，搬家表 #1–#3/#8/#9 的用例编排仍内联在 `Bootstrap/StartupSequence`——profile 前置四步、随包插件 spawn 前安装、启动期告知（版本底线/旧 home/脏退）三类「谁编排」的域职责挂在该挂的层；#6/#10 已随 D1 的 WebSession/Navigation 分部满足（决策在 `Core.WebAuthRecovery`、原语已出根），#5（单实例 socket 拼装）与 #7（`Program.cs` 分支）仍在组合根，触碰即 FULL——归余批 3。
- **G 测试工程归位（LIGHT 子项）**：`BootstrapSettleGateTests`/`PreinstallTests`/`BootstrapGateAndRouterTests` 挂在 Presentation 测试工程，其中 Core 类型（`BootstrapSettleGate`/`PreinstallChoiceGate`/`RuntimeBootstrapGate`/`PresetPluginCatalog`）的测试测谁不归谁。
- **G InternalsVisibleTo 收窄**：Core 的 `InternalsVisibleTo` 开了 5 家（含 shell），`Core.ExitPipeline` 是 `internal` 却被 shell 直接 `new`——编译器边界被自己放宽。

## Decision

1. **三个用例编排下沉 Infrastructure**（零行为变更，语句序/分支/异常边界/日志字符串逐一对应）：
   - `ProfileLifecycle.EnsureReady()`（Platform）——profile 前置四步编排 + 命名收口 catch；与各步实现（`DesktopProfileBootstrap`/`PluginProfileTransaction`）同层共址。
   - `CompanionPreSpawn.EnsureInstalled(bootstrapNeeded, launch)`（Bootstrap）——非引导路径的随包插件安装 + 策略谓词（引导待执行/dev 覆盖共享 home 则跳过）；调用时点契约（宿主已建、spawn 前）由启动主链位置与 doc 注明。
   - `StartupNoticeService.RunAsync(ct)`（Bootstrap）——启动期告知判定编排（落定等待→版本探测/底线→旧 home 留痕→脏退横幅）；横幅构建与推送是展示面，经两个委托闭包接线（与 UpdateCoordinator 的 UI 交接闭包同型）。
   - 编排服务 Run 主链只留三行调用；`CompositionRootSequenceTests` 主链锚点同步更新（源序即契约）。
2. **两处小判定升 Core**：`PathIdentity.PathsEqual`（原编排器私有 `PathsEqual`）与 `UiCopy.RecoveryReason`（恢复页原因选择域决策单点）；尾部截断数（`TakeLast(12)`）判为展示面，留编排侧本地常量。
3. **IIV 收窄到「shell 摘除 + Infrastructure 保留」**：`ExitPipeline` 转 public（唯一 shell 消费点）后摘除 shell 条目；Infrastructure 条目保留并注明理由——它是 Core 端口的适配器层（信任边界内），消费面为 `UpdateJsonContext`/`UpdateOptions.Parse`；摘除需把 IPC 帧契约类型 `UpdateStateFrame`/上下文转 public（`AppJsonContext` 同型仍 internal），API 面反而扩大。
4. **测试按「测谁归谁」拆归**：`BootstrapSettleGateTests` 整搬 `Core.Tests/Bootstrap/`；`RuntimeBootstrapGateTests`/`PreinstallChoiceGateTests`/`PresetPluginCatalogTests` 从混装文件拆出归 Core.Tests；shell 侧留 `BootstrapCommandRouterTests`（路由契约，Ryn.Ipc 面）与 `PreinstallTests`（路由/帧/流式行泵）；新增 `PathIdentityTests`。

## Alternatives considered

- **三个编排并入 `FirstBootBootstrapService`**（方案原文对 #2 的建议）：`Preflight.Bootstrap` 经 Core 端口 `IFirstBootBootstrap` 类型化，concrete 方法调用需 cast；端口加 `EnsureCompanionBeforeSpawn(LaunchOptions)` 则把 Infrastructure 配置类型拖进 Core 端口签名。独立静态 `CompanionPreSpawn` 与孪生实现（引导路径 `InstallBootstrapPluginsAsync`）互相引用，职责同样共址。落败——不为绕 cast 污染端口。
- **版本底线判据（`RuntimeVersionGate`）迁 Core**（方案原文 #3「Core 判据」）：探测（dsh 进程）与底线比较同文件且已有 `RuntimeVersionGateTests` 覆盖；拆 Core 仅增边界不解锁测试、不消除重复决策（MVP 判据均不命中）。落败——本批把判定移出编排即达成「谁编排归位」，判据家不动。
- **IIV 收窄到只剩测试工程**：见 Decision 3 的 API 面扩大论证。落败（部分采纳：shell 侧完成收窄）。
- **导航原语（#10）再抽 Presentation 服务**：原 #10 的驱动是「不占根的分部名额」，D1 已随编排整体出根（`StartupSequence.Navigation.cs`）；进一步抽服务无测试解锁价值（需 window accessor fake 而现无消费方）。落败——判「已满足」，随本批 ADR 记账。

## Consequences

- `StartupSequence` 主链 252→~150 行，三类域职责各有单一家；组合根文件零触碰（`verify-compose-root` 四查、F3 计数不变过）。
- Core 对 shell 的 internal 泄漏清零（编译器强制）；`ExitPipeline` 升公共 API（XML doc 契约已在）。
- 测试基线 867→870（+3：`PathIdentityTests`；拆分零增减），基线家 `scripts/test-baseline.json` 与 README 双语徽章已随批同步，覆盖率持平 60.45%。
- 余批 2（§4.4 `ProxyHeaderPolicy` 安全不变量 + 行为钉回归）、余批 3（#5/#7 + PayloadSmoke 移 `tools/`，FULL 面）随后独立执行。

## Related

- 总纲：`implemented/process/2026-09-27-post-packaging-churn-restructure`（本批为余批 1；总纲随余批 3 完成后改写 implemented）
- 编排形态分离：`implemented/architecture/2026-09-28-compose-root-form-separation`
- UI 交接委托闭包先例：`implemented/architecture/2026-09-28-update-coordinator-core-port`
