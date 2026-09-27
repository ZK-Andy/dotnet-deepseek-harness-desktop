# Agent Note: 回环代理源承载 dsh 内容（替代自有 scheme 转发）

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

Related: 前序 `bug-fix/2026-09-27-shell-mint-and-forward`（传输面被本篇替代，铸币模型保留）+ `testing/2026-09-26-page-verdict-gate` + `testing/2026-09-26-smoke-witness-real-and-eval-first-hop` + `bug-fix/2026-09-12-port-drift-ipc-origin-mismatch` + 上游 `Yupmoh/Ryn v0.38.0`（`IpcProtocol`/`LocalWebServer`/`RynWindow.LoadContent`/`RynSchemeResponse`，源码逐行验真，版本与钉选一致）+ 上游 `deepseek-ai/deepseek-harness`（`boot-client.ts` 激活判据、`dsh-client-connection` 相对 RPC、`dsh-client-hmr` 相对 SSE）。

## Problem

自有 scheme 转发（`dsh-app://app` + handler 逐请求转发）在 mac 真机上无法启动 dsh UI（dispatch `36275191770` arm64：dsh 页渲染但 `web boot: 54 entries did not activate`，截图实证），且三条死路都是结构性的、与等待时间无关：

1. **SSE 必缓冲**：Ryn serving path 只收 contiguous stash（上游 `RynSchemeResponse` 源码原话，serving path 必物化）——`/plugins/events` 经 scheme 永不能流式到达；dsh 客户端靠该推送拿服务 graph（`remote` 等服务永 pending，09-19 已证推送语义），54 个条目永不激活。
2. **`/ipc/*` 打到 dsh 405**：Ryn 桥 `_ipcBase` 默认为空 = 相对页面 origin（上游桥 JS 原文）；dsh-app 页的 eval 回包/invoke 全进自家 handler 转给 dsh（无此路由，09-26 已定性"无 `/ipc/eval/`"），宿主 `_pendingEvals` 挂死——探针恒超时、verdict 恒 unknown。
3. **POST Content-Type 被转发层丢**：`ByteArrayContent` 建时无头 + `TryAddWithoutValidation` 对 content 头静默跳过（自家注释承认），dsh JSON RPC 收无类型体。
4. 口径纠错：Linux `36274189037` 系空心绿（窗口从未离开 `ryn://app/index.html`，witness 过的是占位白页 mean 0.98）——转发本体从未在 Linux 被真验证；mac arm64 是唯一真 UI 证据。

## Decision

- 新增 `Infrastructure/Runtime/DshLoopbackProxy.cs`（边界层回环转发代理）：`TcpListener` 纯 loopback、端口 OS 分配（`0`，`Run` 早于 `BuildApp` 绑定，日志 loud 实际源）；逐请求向 dsh authority 转发（路由/cookie 沿用 `DshShellForward` 铸币态，新增内部 `TryGetRoute`）；303 内部跟完（沿用 `ResolveFollowTarget`）；`Set-Cookie` 永不回页面；POST 的 `Content-Type` 等 content 头保真（两级 `TryAdd`：请求头不成则落 `Content.Headers`）；SSE/未知长度流式直通（头透传 + 体 `CopyToAsync`，`HttpClient.Timeout` 无限 + 页断联取消）；`Upgrade`/WS 请求 loud 502（dsh 客户端当前只用 fetch/SSE，无需求不做）。
- 窗口 URL 改 `http://localhost:{port}/`：Ryn 走 dev-server 分支（`RynWindow.LoadContent`：IPC-only 服 + `SetAllowedOrigins([devOrigin, ipcBase])` + `SetIpcBaseOverride` + CORS 信任代理源）——IPC/eval/invoke/探针全活，零上游改动。
- 自有 scheme 退役：`DshSchemeBridge` + `ConfigureCustomScheme` + `ShellScheme/ShellOrigin/ShellRoot` 删除（R1 死代码）；探针 `expectedOrigin`、导航守卫允许集、冒烟脚本期望 origin 全部跟转代理源（动态端口，运行时派生；冒烟用 host.log 的代理源行定位）。
- dsh 未起仍 wwwroot 降级（语义不变）；`MintAsync`/铸币三点/脱敏纪律原样保留；`ForwardAsync` 随桥退役（行为测试迁移至代理级，`Mint`/`ResolveFollowTarget` 测试保留）。

## Alternatives considered

- **等上游给 custom-scheme 配 IPC**：落败——周期不可控；回环代理零上游依赖，且语义与 Ryn dev 分支同构（官方为"UI 在外部 loopback 服"预留的正路）。
- **scheme 内伪造 `/ipc/*` 200**：落败——eval nonce/`_pendingEvals` 全私有，伪造破坏 Ryn 状态机且 invoke 拿垃圾回包（fail loud 纪律）。
- **复用 Ryn `LocalWebServer`**：落败——`internal` 无公开面；`UseLocalServer` 分支要求 `Url` 为空，与窗口 URL 互斥。
- **退回 dsh 直连**：落败——mac cookie 墙（H2 定论）原地返回。
- **维持 scheme + 接受 SSE 死**：落败——arm64 实证 UI 无法启动，无可接受降级。

## Consequences

- 代价：自研最小 HTTP/1.1 解析转发（单文件 ~300 行；请求体只认 `Content-Length`，chunked 请求 loud 502）；loopback 信任面（token/cookie 不出本机，与既有模型一致）；SSE 空闲不断（页断联即 cancel，`EventSource` 自重连）。
- 收益：流/RPC/eval/invoke 全活；mint/303/脱敏纪律沿用；三平台同构；诊断 loud 行保留（入口/终态）。
- 欠账：WS `Upgrade` 透传（按需）；响应头最小集（沿用前序 TODO：`Content-Type` 外按需补）。

## Testing

- 代理单测（回环真 socket + 桩 dsh）：SSE 首块渐进到达（后块延迟释放前即收到首块）、POST `Content-Type` 保真、cookie 附带、303 跟进、未铸币 502、`Upgrade` 502；旧桥/`ForwardAsync` 测试随退役删除。
- `dotnet build` 0 警告；`dotnet test` 全绿；FULL 三审收口；mac 双腿 dispatch 验证（到达 + verdict + 见证 + 真 UI 像素）。

## Deferred

- WS 透传：dsh 客户端传输面经本地源码验真（fetch POST RPC + `EventSource` SSE + 可选 worker-local `openStream`），无原生 WebSocket 需求；出现需求再立项。
