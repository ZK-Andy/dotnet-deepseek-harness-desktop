# Agent Note: 回环代理源承载 dsh 内容（替代自有 scheme 转发）

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok
Review: FULL/2026-09-27#2/R1=ok R2=ok R3=ok

Related: 前序 `bug-fix/2026-09-27-shell-mint-and-forward`（传输面被本篇替代，铸币模型保留）+ `testing/2026-09-26-page-verdict-gate` + `testing/2026-09-26-smoke-witness-real-and-eval-first-hop` + `bug-fix/2026-09-12-port-drift-ipc-origin-mismatch` + 上游 `Yupmoh/Ryn v0.38.0`（`IpcProtocol`/`LocalWebServer`/`RynWindow.LoadContent`/`RynSchemeResponse`，源码逐行验真，版本与钉选一致）+ 上游 `deepseek-ai/deepseek-harness`（`boot-client.ts` 激活判据、`dsh-client-connection` 相对 RPC、`dsh-client-hmr` 相对 SSE）。

## Problem

自有 scheme 转发（`dsh-app://app` + handler 逐请求转发）在 mac 真机上无法启动 dsh UI（arm64 dispatch 实证：dsh 页渲染但 `web boot: 54 entries did not activate`，截图实证），且三条死路都是结构性的、与等待时间无关：

1. **SSE 必缓冲**：Ryn serving path 只收 contiguous stash（上游 `RynSchemeResponse` 源码原话，serving path 必物化）——`/plugins/events` 经 scheme 永不能流式到达；dsh 客户端靠该推送拿服务 graph（`remote` 等服务永 pending，09-19 已证推送语义），54 个条目永不激活。
2. **`/ipc/*` 打到 dsh 405**：Ryn 桥 `_ipcBase` 默认为空 = 相对页面 origin（上游桥 JS 原文）；dsh-app 页的 eval 回包/invoke 全进自家 handler 转给 dsh（无此路由，09-26 已定性"无 `/ipc/eval/`"），宿主 `_pendingEvals` 挂死——探针恒超时、verdict 恒 unknown。
3. **POST Content-Type 被转发层丢**：`ByteArrayContent` 建时无头 + `TryAddWithoutValidation` 对 content 头静默跳过（自家注释承认），dsh JSON RPC 收无类型体。
4. 口径纠错：同日 Linux run 系空心绿（窗口从未离开 `ryn://app/index.html`，witness 过的是占位白页 mean 0.98）——转发本体从未在 Linux 被真验证；mac arm64 是唯一真 UI 证据。

## Decision

- 新增 `Infrastructure/Runtime/DshLoopbackProxy.cs`（边界层回环转发代理）：`TcpListener` 纯 loopback、端口 OS 分配（`0`，`Run` 早于 `BuildApp` 绑定，日志 loud 实际源）；逐请求向 dsh authority 转发（路由/cookie 沿用 `DshShellForward` 铸币态，新增内部 `TryGetRoute`）；303 内部跟完（沿用 `ResolveFollowTarget`）；`Set-Cookie` 永不回页面；POST 的 `Content-Type` 等 content 头保真（两级 `TryAdd`：请求头不成则落 `Content.Headers`）；SSE/未知长度流式直通（头透传 + 体 `CopyToAsync`，`HttpClient.Timeout` 无限 + 页断联取消）；`Upgrade` 经裸 TCP 隧道直泵（对齐上游 `onBeforeSendHeaders` 手术：`Host`→dsh authority、`Origin`/`Referer`→dsh 自源 + 贴 cookie + `sec-fetch-site: same-origin`，页源三值零透传；上游首块解状态行 loud 留痕后原样前送；寿命与连接绑定，无计时器——清单与拒因留痕见 [upgrade-tunnel-host-authority](../bug-fix/2026-09-27-upgrade-tunnel-host-authority.md)）；源头改写为 dsh 自源（`Origin`/`Referer`，标准反代语义；页源代理 URL 触发 dsh 网关 403，dispatch 实证）；畸形请求判别式 loud 502（超限/分块/绝对目标，预连接静默关）。
- 代理本地端点（无需铸币，对齐上游 `serveWebDocument` 的本地文档面）：`/__shell_ready`（已铸币 200，未铸币长轮询等铸币门——无计时器，中止即页断联/退出）；`/__shell_guide/*`（wwwroot 磁盘页，GET/HEAD，越界 403/缺失 404/他法 405，MIME 对齐上游子集）；未铸币的 `/` 与 `/index.html`（英文极简 holder，自 `fetch` 就绪后自 `reload`，无计时器；中文指南一链之隔，词典零负担）。
- 窗口 URL 恒为代理源（含 dsh 未就绪时；仅代理绑定失败回退 wwwroot）：Ryn dev-server 分支在窗口创建时接管 IPC（`_ipcBase` 绝对化 + CORS 信任），不依赖 dsh 时序——冷机探针同样有效；`EnterMainUiAsync` 只做铸币（holder 自 reload，启动链零 host 导航，绕开 saucer `set_url` 原生挂家族）。
- 自有 scheme 退役：`DshSchemeBridge` + `ConfigureCustomScheme` + `ShellScheme/ShellOrigin/ShellRoot` 删除（R1 死代码）；探针 `expectedOrigin`、导航守卫允许集、冒烟脚本期望 origin 全部跟转代理源（动态端口，运行时派生；冒烟用 host.log 的代理源行定位）。
- dsh 未起仍 wwwroot 降级（语义不变）；`MintAsync`/铸币三点/脱敏纪律原样保留；`ForwardAsync` 随桥退役（行为测试迁移至代理级，`Mint`/`ResolveFollowTarget` 测试保留）。

## Alternatives considered

- **等上游给 custom-scheme 配 IPC**：落败——周期不可控；回环代理零上游依赖，且语义与 Ryn dev 分支同构（官方为"UI 在外部 loopback 服"预留的正路）。
- **scheme 内伪造 `/ipc/*` 200**：落败——eval nonce/`_pendingEvals` 全私有，伪造破坏 Ryn 状态机且 invoke 拿垃圾回包（fail loud 纪律）。
- **复用 Ryn `LocalWebServer`**：落败——`internal` 无公开面；`UseLocalServer` 分支要求 `Url` 为空，与窗口 URL 互斥。
- **退回 dsh 直连**：落败——mac cookie 墙（H2 定论）原地返回。
- **维持 scheme + 接受 SSE 死**：落败——arm64 实证 UI 无法启动，无可接受降级。

## Consequences

- 代价：自研最小 HTTP/1.1 解析转发（成帧/本地/隧道三类拆分，各 ≤400 行；请求体只认 `Content-Length`，chunked 请求 loud 502）；loopback 信任面（token/cookie 不出本机，与既有模型一致）；SSE 空闲不断（页断联即 cancel，`EventSource` 自重连）。
- 收益：流/RPC/WS/eval/invoke 全活（WS 为裸 TCP 隧道 + 头手术）；mint/303/脱敏纪律沿用；三平台同构；诊断 loud 行保留（入口/终态/升级面三步）；响应头最小集仍按需（沿用前序 TODO）。

## Testing

- 代理单测（回环真 socket + 桩 dsh）：SSE 首块渐进到达、POST `Content-Type` 保真、源头改写回归、WS 隧道（101 + 握手手术）、holder/就绪长轮询/指南磁盘面、303 跟进、未铸币/分块畸形 502、请求构造直测；旧桥/`ForwardAsync` 测试随退役删除。
- 冒烟落定门收紧（`nav_count_after_ready` 只认 localhost/127 壳到达）：占位到达不再算落定（4 次占位即落定的 dispatch 实证）；重绘窗 3→15s（dsh 冷启动约 10s，裁决后白页实证）；见证加客户端存活 OR 门（代理 `200 json/SSE` ≥3 即活；浅色真 UI mean≈0.99/sd≈0.04 与白墙像素不可分，arm64 dispatch 实证）；mac 失败文案去"①缺失"特指。
- `dotnet build` 0 警告；`dotnet test` 全绿；FULL 三审收口（R2 五条全收口 + LIGHT 跟进 + 本轮 FULL 终案）；mac 双腿 dispatch 再验证。

- 历程 dispatch 痕迹：run `36275191770`（SSE 激活失败）/`36274189037`（空心绿）/`36294149610`（页源 403 + 占位落定）/`36295632426`（见证像素不可分）为 Problem/Decision 各机制的实证 run，可查不占正文。

## Deferred

- Ryn 原生 IPC 的运行期导航场景：窗口初始 URL 恒代理源后 dev-server 分支覆盖冷/热全部启动（`_ipcBase` 绝对化自出生）；收养/恢复等运行期导航仍是相对源页面（公开面无补救），上游 `Yupmoh/Ryn#102` 跟踪中——影响面仅运行期重导航后的探针，见证门承担。
