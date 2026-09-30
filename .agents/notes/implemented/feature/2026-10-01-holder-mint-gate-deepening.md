# Agent Note: 铸币门控加深——稳定化探活 + holder 过渡屏同款化

Status: implemented

Review: LIGHT/2026-10-01#1/R2+R3=ok（0 Blocker；Suggestion 3 条全采纳：门控成功路径钉死测试、fail-open 日志记实际耗时、并发覆盖面记账入 Consequences）

## Problem

进程重启（插件市场触发为主）后，用户先看到页面、会话树要再等一会才加载。壳侧链路：holder 页长轮询 `/__shell_ready`，`DshShellForward.MintAsync` 对 token URL 收到 303 + set-cookie 即放行（`_mintedTcs.TrySetResult()`），holder 收 200 后 `location.reload()` 进 dsh web。缺口：**303 只证认证行挂载，不证前端静态面就绪**——dsh 先 bind 端口、认证 + 前端静态由更晚挂载的行提供（`RuntimeLineageProbes` 注释与 ADR relay-web-readiness 实证），reload 落进半成品页面，dsh web 随后的自举/握手/列表拉取全部暴露为「页面在、树不在」的空窗。收养链（接力重启）已有 `RelayWebReadinessGate` 稳定窗判据，但冷启与铸币链未用——同一性质的判据缺口分居两处，只修了一处。

## Decision

1. **铸币门控加深（`DshShellForward.MintAsync`）**：303 + set-cookie 成功后进入稳定化阶段——以铸得 cookie 对 dsh origin `GET /` 轮询探活（复用 `BuildForwardRequest` 单源构造），Ready 判据 = 200 + 响应体非空；采样喂 `RelayWebReadinessGate`（Ready 连续维持达稳定窗、任一非 Ready 清零，与收养链同源同参）。稳定通过或预算耗尽才写 route（authority + cookie）并 `_mintedTcs.TrySetResult()`。
2. **预算封顶、到期 fail-open**：稳定化设总预算（默认 30s），超预算未稳定则照常铸币并 loud 记日志（`[shell] 就绪稳定化预算耗尽…fail-open`）。理由：dsh 挂死场景由监督器/恢复面兜底，铸币不得变成无界等待；fail-open 后的页面形态与今日一致（不劣化）。
3. **参数为内部常量 + 实例级测试缝**：稳定窗 2s、轮询节拍 1s、预算 30s（稳定窗/节拍与收养链探测同参），经 internal 构造缝注入覆写（对齐 `RelayDelayOverride` 先例），不进 RuntimeTimeouts/配置模型（非用户可调行为）。
4. **holder 过渡屏同款化（`DshLoopbackLocal.HolderPage`）**：从两行裸文案升级为恢复页（`RecoveryPageBuilder.Skeleton`）同套 `--dshdt-*` 内嵌回退调色板——亮基暗覆 + Canvas 底 + conic-gradient spinner + 居中排版；文案保持纯英文（`DshLoopbackLocal.cs` 不进 `verify-ui-copy` 消费清单，无 CJK 即无对账负担）。既有契约逐项保留：首行可见文本含 `WebAuthRecovery.HolderMarker`（裁决排除判据）、`/__shell_ready` 长轮询无计时器（禁 `setTimeout`/`setInterval`）、`online`/`visibilitychange`/手动重试链、`/__shell_guide/` 链接。
5. **调用点不动**：铸币同步阻塞（`GetAwaiter().GetResult()`）仅存在于启动主链（`StartupSequence.StartRuntime`，位于窗口消息循环之前，其前已有分钟级 spawn 等待）与监督任务（无同步上下文）；稳定化典型只延续 dsh 静态面挂载所需的秒级时长，预算封顶上界有限。

## Alternatives considered

- **门控放调用点而非 `MintAsync` 内**：rejected——铸币有四个调用点（冷启/引导落定/收养重铸/鉴权自愈），逐点重复稳定化逻辑且易漏新增点； MintAsync 是「铸币就绪」语义的唯一家，放行判据属其内聚职责。
- **壳侧隐藏 WebView、等 ready 再揭窗（官方桌面端揭窗门控形态）**：rejected——我们的页面恒驻壳代理 origin，holder 页本身就是揭窗前的过渡面；再叠一层窗口隐藏只会让用户在无反馈黑窗里等待，劣于可见的过渡屏。
- **上游 dsh web 做会话列表 localStorage 快照 stale-while-revalidate**：archived 方向——能消掉页内自举残留（壳侧不可见的 1–2s），但需上游协作，另立 issue 推动；本批不依赖。
- **探活判据加内容断言（如校验 HTML 含特定标记）**：rejected——与 dsh 内部页面形态耦合，上游改版即误判；200 + 非空体已足以证「前端静态面挂载」，内容级健康由页面健康探针（`WebAuthRecovery` 裁决）接管，分层不越位。

## Consequences

收益：reload 落地时 dsh web 静态面必然已挂载（预算内），「页面先出、树慢出」的主空窗被 holder 过渡屏吸收；冷启/收养/自愈四条铸币链的放行判据归一；holder 与 boot/恢复页视觉同族。代价：铸币放行整体后移（典型秒级，上限 = 预算 30s，仅 dsh 半死时触达）；`MintAsync` 单次调用时长方差变大（同步阻塞调用点已核查，见 Decision 5）；holder 页多 ~1KB 内嵌 CSS。并发覆盖面放大（评审记账）：route 覆盖式语义不变，但慢路铸币者的覆盖动作可被稳定化拉宽至预算上界——前驱已死场景下可能用一个过期 cookie 覆盖先铸的新 route，由自愈链再次铸币收敛（影响有界，不新增状态）。

## Testing

`DshShellForwardTests`：稳定化通过路径（origin 即刻 200 → 铸币 + 快速返回）、预算耗尽 fail-open 路径（origin 恒 401 → 预算内不铸币、到期铸币 + 日志含 fail-open 标记）、探活请求携带铸得 cookie；既有 303 失败/脱敏/取消语义不动。`DshLoopbackProxyTests`：holder 内容断言更新（`--dshdt-`/spinner 新增，`HolderMarker`/无计时器/ready-reload 契约保留），桩补齐稳定化探活面（origin 200）。`dotnet test` 全绿（901/901：170+196+535，含评审补强的门控成功路径钉死 `Mint_WebFaceReadyAfterDelay_PassesGateWithoutFailOpen`）；`verify-adr-format`/`verify-md-links`/`verify-review-tier` 按门禁跑。

## Related

- [relay-web-readiness](../bug-fix/2026-09-16-relay-web-readiness.md)（稳定窗判据来源）。
- [shell-proxy-port-persistence](../architecture/2026-09-30-shell-proxy-port-persistence.md)（holder/代理 origin 常驻形态）。
- [boot-page-dsh-design-alignment](./2026-09-30-boot-page-dsh-design-alignment.md)（`--dshdt-*` 调色板同源）。
