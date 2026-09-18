# Agent Note: market-restart-adopt-first

Status: implemented

Review: FULL/2026-09-15/R1=ok R2=ok R3=ok

## Problem

插件市场更新插件后的一键重启是「两个重启者的端口竞赛」：dshmarket 的自重启 helper 杀掉在管 dsh 后原地拉起续任者（同一端口），而桌面监督器把「子进程退出」当成崩溃，也按同一端口 spawn 自己的 dsh。实机 host.log（2026-09-15 17:47:40–17:48:27）：壳 spawn 的 dsh 插件树加载 40s+ 后 bind 失败（EADDRINUSE），才落到既有交接处置收养市场的续任者——用户可见 46 秒恢复屏、两次 WebView 导航（页面自恢复一次 + 最终导航一次）、一个注定失败的竞争进程。

## Decision

进程内重启入口在 spawn 前先给市场接力一个**共存窗口**：滚动扫描血统残留（`FindLineageResidue`），存在「接力证据」就继续等待，等续任者接管首选端口**且 web 面可服务并连续维持满稳定窗**后直接走既有 `RecoverFromFailureAsync` 收养链（就绪判据见 [relay-web-readiness](../bug-fix/2026-09-16-relay-web-readiness.md)）——跳过竞争 spawn，恢复一次落地（单进程、一次导航）。证据判据由两层构成：可证诞生于被监督运行时之后（`RuntimeLineage.IsRelayEvidence`，helper 或续任者），**且父链存活**（临终 dsh 生前自拉起、继承 token 的孤儿主体——PTC worker/pnpm 子壳——不是接力的正主，不得当固定证据拖到预算上限）；只有 helper 类证据支持宽限窗的 sticky 保持。三条退出线保证不拖累普通恢复：①证据消失（helper 已死且无新生续任者，helper 是续任者的父进程，它死且子代不在场即接力中止）立即回落；②总预算与 `RestartAsync` 恢复时限同源，无条件兜底回既有 spawn 侧（其 bind 预探测仍兜底）；③helper 从未出现时宽限窗（2s）耗尽即回落——崩溃恢复路径的额外等待不超过一个宽限窗。判定收口为纯函数 `RuntimeLineage.ShouldKeepWaitingRelay`（可机测），副作用探针（web 面就绪/残留枚举/延迟）中，后两者留内部注入口供测试闭环（就绪探针由真实 HTTP 夹具驱动，无注入口）。

- 接线点在 `HarnessRuntimeHost.StartInnerAsync` 的进程内重启分支（`supervisedStart` 非空即监督型重启），冷启动不受影响；接力命中时**绝不 spawn**，既消除注定失败的竞争进程，也消除第二次导航。
- 收养后的启动成功收敛（`HarvestLineageResidue`）不变：helper 是续任者的祖先，`ProtectedByRuntime` 双向保护已覆盖，不会被连坐击杀。

## Alternatives considered

- **提前 `Adopt` 判据前置（在监督器看到子进程退出时立即收养，不等续任者 bind）**: 落败——续任者 bind 前没有 pid 之外的稳定证据可判「同一接力」（`PlanPortConflict` 的收养不变量要求端口在服务 + 可证新生），提早收养会把尚在加载期/已死对象的候选收进来，破坏既有判据的零误收构造。
- **抑制宿主侧重启（监督器检测到 helper 在场就纯等，不设预算）**: 落败——helper 卡死或 pnpm 失败时恢复链会无限挂起，比现状更糟；预算无条件上限定为兜底不变量。
- **市场侧改动（上游 dshmarket 改为通知宿主）**: 落败——依赖上游节奏且市场对「未识别为受管宿主」的 dsh 保留自重启属产品行为，壳侧单边修复自足。

## Consequences

监督型重启在市场接力场景下：单进程、一次导航落地（导航推迟到续任者 web 面就绪**并连续维持满稳定窗**，见 [relay-web-readiness](../bug-fix/2026-09-16-relay-web-readiness.md)）、不再出现约 40s 的注定失败窗口；崩溃（无 helper）场景增加最多一个 2s 宽限窗的延迟；`RuntimeSupervisor` 与 `StartInnerAsync` 既有端口冲突处置完全复用，无新增状态（helper 见过与否仅存活在本次等待循环内）。观测面新增 host.log 留痕：接力接管命中（「市场接力续任者已接管首选端口 …」）、就绪等待（「已监听但 web 面未就绪」，见 relay-web-readiness）与两条回落原因线（接力中止 / 宽限窗耗尽，与接管行对偶）。

## Related

- [收养市场接力的 dsh 续任者（运行时交接）](../bug-fix/2026-09-12-runtime-handoff-adoption.md)：本决策落地其 Deferred「以 helper 出现作为接力意图信号」，复用其收养判据与收割面保护（helper 是续任者的祖先，不动）；血统判据、端口签名词法、判活 token 复验均以彼为准。
- [接力就绪判据升级为 web 面可服务](../bug-fix/2026-09-16-relay-web-readiness.md)：本决策的就绪判据由「端口可连」收紧为「web 面已应答」，补救本决策按端口接管即导航造成的空白页窗口。
- [bind 预探测压缩端口等待](../architecture/2026-09-13-port-wait-compression.md)：其 bind 预探测在本决策后只在接力回落路径生效——relay 命中的启动尝试整段跳过，bind 探测的「dsh 加载整树才 bind」事实正是本决策共生窗口成立的前提（续任者也需同样时长 bind，窗口才有等待价值）。
