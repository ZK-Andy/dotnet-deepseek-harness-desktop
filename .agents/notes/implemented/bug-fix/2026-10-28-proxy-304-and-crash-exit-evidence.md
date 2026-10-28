# Agent Note: 代理透传 304 + 崩溃证据补退出码

Status: implemented

Review: FULL/2026-10-28/R1=pending R2=pending R3=pending

## Problem

2026-10-28 11:36 实机（壳 0.7.9 / dsh 0.2.2-alpha.2）：插件市场更新后 dsh 子进程退出。事后读 `host.log` 只能得到两件事——stderr 尾部两行常规模型列表（`llm-deepseek-account` / `llm-deepseek-api-key`，与死因无关），以及「第 1 次连续崩溃」。**没有退出码**，于是「被信号杀」与「自己退出」不可判别，事故停在「时序可疑」，无法归因。

同一窗口还暴露第二个缺陷：收养续任者后，页面发起的三个 `/assets/*` 请求全部落成 502，日志为「代理跟进终止（hop=0，location=无）」。现场复现确证成因：

```
直接打 dsh（带 If-None-Match）      → 304 Not Modified
经壳代理（带 If-None-Match）        → 502 Bad Gateway（34 字节）
同一路径不带 If-None-Match 经壳代理 → 200 OK
```

`DshLoopbackProxy.FollowRedirectsAsync` 用 `status is < 300 or >= 400` 判定终态，把 304 归进重定向分支；304 按定义不带 `Location`，于是必然落进「无 Location → 502」。而 dsh 对静态资源发 `ETag`（实测 `/assets/*` 恒带 `W/"..."`），页面缓存复验即触发——**每个条件请求命中都变成 502**。

这一点与既有决定直接冲突：[冷启动代理缓存头白名单](2026-10-26-cold-start-wait-collapse-and-proxy-cache-headers.md) 的 C1 特意把 `ETag` 放进回包白名单，目的正是让 WebKit 持久缓存告别启发式、不再冷启动重放旧 SPA。但透传 `ETag` 等于允许页面发条件请求，而条件请求一命中就 502——**C1 的意图直到本批才真正成立**；此前它给页面发了缓存凭证，却让凭证兑现的那一步失败。这不是市场更新的次生现象，而是常驻缺陷，被一次重载暴露。

## Decision

两条独立修复，同一批落地：

1. **304 归终态**。`FollowRedirectsAsync` 的重定向判定收敛为显式集合 `IsRedirect(status) = 301/302/303/307/308`（唯一家），304 与其余非 3xx 一并直交页面。回包仍走既有路径：`ProxyHeaderPolicy.ResponseCacheHeaders` 的白名单已含 `ETag`，`ShellProxyFraming.Reason` 已认 304，故页面侧拿到 `304 + ETag`，缓存语义完整。
2. **崩溃证据补退出码**。`IRuntimeHost` 增加 `int? LastExitCode`（与 `StderrTail` 并列，端口只扩监督器消费集）。实现三处写入点：
   - `Exited` 回调取码（**不**在 `StopCore` 回收后读——那时读到的是自己那一刀的结果）；
   - `WaitForExitAsync` 在 `_process` 已退出时补记（进程在挂事件前就死时 `Exited` 永不来，原 `is not { HasExited: false }` 分支会让退出码永远丢失）；
   - 每次新 spawn 清空旧观测（`ClearLastExit`），防重启后读到上一次的退出码。

   监督器 `LogStderrTail` 改为 `LogExitEvidence`：先落退出码行（带 POSIX 信号判读：137=SIGKILL、143=SIGTERM、134=SIGABRT、139=SIGSEGV、0=正常退出，其余「非零退出」），再落 stderr 尾部；stderr 为空时落「空」而非省略整行。取不到码时如实写「未取得」，**不伪造 0**。收养路径（非本进程子进程、拿不到 `ExitCode`）单独留「已退出，无退出码可取」。

## Alternatives considered

- **按 3xx 全量跳过、让页面自己处理重定向**：落败——现有 `Proxy_RedirectLoop_StopsWith502` 与「不把裸 3xx 交给页面」的既定契约要求壳内跟进；只把 304 摘出去即可，不必推翻跟进面。
- **用 `300 <= status < 400 && Location is not null` 作判据**：落败——语义上「有 Location 才跟进」看似等价，但 304 之外的畸形 3xx（300/305/306）也会被静默放行给页面；显式集合让「哪些码算重定向」成为可读的封闭集，新增码需显式加入。
- **退出码从 `/proc` 或 `waitid` 另取**：落败——`Process.ExitCode` 已是权威来源，另立通道要处理跨平台差异与权限面，与本缺口不成比例。
- **把完整 stderr 落盘到独立文件而非内存尾部**：本轮落败——`HostLog.MaxBytes = 5MiB` 且只留一代，独立文件会引入新的滚动策略与磁盘配额问题；退出码先解决「有无证据」，stderr 容量是另一件事。

## Consequences

收益：崩溃可归因从「一片干净的 stderr」升级到「退出码 + stderr」二元证据；条件请求恢复正常，`If-None-Match` 命中不再 502。代价：`IRuntimeHost` 端口扩了一个成员，三个测试 fake 需同步（已改）；退出码证据只覆盖本进程 spawn 的子进程，收养的续任者仍无码可取（记「已退出」是这条路径的证据上限）。

未覆盖的残留：退出码进 `host.log`，但**不进**失败面对话框正文——`RecoveryDialogText.Build` 只收 `StderrTail`，原生模态有 600 字符预算且按钮会被顶出屏幕（2026-10-15 教训）。用户可见面暂不含退出码，排障仍需读日志。

## Testing

- `Proxy_NotModified_PassesThroughInsteadOf502`：桩 dsh 对 `/cached` 回 304 + `ETag`，断言页面收到 304（非 502）、`ETag` 透传、日志无「跟进终止」。修复前该用例红。
- `RunAsync_LogsExitCodeAlongsideStderrTail` / `RunAsync_MissingExitCode_SaysUnavailable`：断言退出码与 stderr 同轮落盘、缺失时写「未取得」与「空」。
- 相关面全绿：`DshLoopbackProxyTests` 39、`RuntimeSupervisorTests` 7、`HarnessRuntimeHostTests` 27、Core 监督/起步 17。

## Related

- ADR loopback-forward-proxy（代理转发面本体，本批只改其重定向判定）。
- ADR cold-start-wait-collapse-and-proxy-cache-headers（C1 透传 `ETag` 的意图由本批兑现：条件请求命中不再 502）。
- ADR proxy-log-noise-reduction（终态行与失败计数的既有契约）。
- ADR runtime-supervisor-core-port（`IRuntimeHost` 端口来源；本批按 MVP 判据扩 `LastExitCode`）。
