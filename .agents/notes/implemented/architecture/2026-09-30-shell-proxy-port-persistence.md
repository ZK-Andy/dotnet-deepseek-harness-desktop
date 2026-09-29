# Agent Note: shell-proxy-port-persistence（代理端口持久化——会话恢复回归修复）

Status: implemented

Review: FULL/2026-09-30/R1=ok R2=ok R3=ok

详情：R1 首轮 0 Blocker + 3 Suggestion、R2 首轮 0 Blocker + 2 Suggestion + 1 定向核（TIME_WAIT），R3 首轮 1 Blocker + 3 Suggestion——建议书面裁定后一次性收口（REUSEADDR/委托契约/ReserveFreePort/Erratum 家族本批采纳，公共 ctor 收单等挂账），R3 验轮 1 轮 0/0 闭合。

中文（双语暂不启用）。

## Problem

2026-09-27 回环代理上源（`implemented/architecture/2026-09-27-loopback-forward-proxy`）引入一处 silent regression：**桌面应用每次重启都打开新会话，不再恢复上次会话**。

回归链（全部实据）：

1. 2026-08-21 冷启动复用上次 dsh 端口（`.dsh-web-port`）：彼时窗口直连 dsh origin，端口稳定 → dsh web 的 `restoreSelection`（读 `localStorage["dsh.sessions.current"]`，按 origin 隔离）命中 → 恢复上次会话。
2. 代理上源后窗口 URL 改为回环代理源，代理用 `TcpListener(port 0)` 每次随机绑定 → **页面 origin 每次重启漂移** → localStorage 按 origin 隔离全部丢失 → `restoreSelection` 落到「复用空白或新建」。dsh 端口持久化本身未坏（仍在服役，服务 run 内重启同端口语义），坏的是页面 origin 侧。
3. 法证佐证：`~/.local/share/deepseek-harness-desktop/storage/` 积累 56 个 origin 目录，每次重启遗留一份孤儿 `dsh.sessions.current`（存量已于 2026-09-30 一次性清理）；host.log 实测 `/api/session/list` 返回后立刻 `/api/session/create`（恢复失败 → 新建路径）。
4. 误导源：`HarnessRuntimeHost.Paths.cs` 的 `TryLoadPersistedPort`/`PersistPort` 注释至今描述「恢复上次会话」链路，09-27 后已 stale（页面 origin 已换家到代理）。

scheme 方案（`dsh-app://app` 自定义协议，官方 Electron 同款）本轮再验证仍不可行：HTTP 侧可行（saucer `saucer_webview_handle_scheme` 已被 Ryn 使用），但 **WS 被服务端信任栅栏拦死**（上游 `api-request-trust.ts` sec-fetch-site cross-site 一票 403、Origin.host 必须等于 Host 头），WebKitGTK 无 `onBeforeSendHeaders` 等价物，saucer 不支持加载 WebExtension——本批不做（远期对齐官方架构另立项）。

## Decision

代理端口沿用 08-21 dsh 端口的「持久化 + 冲突漂移再记忆」语义：

1. **profile state 新文件 `.dsh-shell-port`**（`ResolveProfileStatePath` 族，与 `.dsh-web-port` 同目录同族）：`HarnessRuntimeHost` 新增 `ResolveShellPortFilePath` + `TryLoadShellPort`/`PersistShellPort` 对（镜像 dsh 端口对；代理端口记忆无旧版位置，不设迁移回读）。
2. **`DshLoopbackProxy` 绑定序列改造**：读记忆 → 有记忆即试绑该端口（成功即用，值不变不写盘）→ 被占（`AddressAlreadyInUse`）loud 日志 + OS 重分配 + **新端口立即持久化** → 无记忆/文件损坏 OS 分配 + 持久化。非 Windows 平台 listener 绑定前预设 `SO_REUSEADDR`（壳侧主动关闭的页连接留 TIME_WAIT，快速重启试绑记忆端口否则假性 EADDRINUSE；Linux/macOS 下不放开对存活 LISTEN 的冲突，被占降级语义不变；Windows 的 REUSEADDR 有劫持语义故不设）。其余绑定异常（非占用类）维持既有 fail loud/降级 wwwroot 面不变。
3. **文件缝经 `TryCreate` 可选委托注入**（缺省即上条静态对）：`TryCreate` 的既有测试与新增行为测试都以临时委托密闭，不触真实 DSH_HOME。
4. **stale 注释家族同批改写**：`TryLoadPersistedPort`/`PersistPort` 注释与 `HarnessRuntimeHost.StartAsync` remarks 中「端口稳定 → 恢复上次会话」链路改为现语义（dsh 端口记忆服务 run 内重启同端口与交接判据；页面会话恢复的 origin 稳定性由代理端口记忆承担）；[docs/architecture.md](../../../../docs/architecture.md) 启动模型节跟值。

已知局限（与 dsh 端口漂移「观测位不是修复位」同口径）：真实端口冲突的那一次重启 origin 漂移一次，丢一次恢复；此后新端口被记住，恢复链重新闭合。Windows 另有 TIME_WAIT 边缘：不设 REUSEADDR（劫持语义），60s 内快速重启可能假性 EADDRINUSE 触发一次无谓漂移。

## Alternatives considered

- **自定义 scheme（`dsh-app://app`，官方 Electron 同款）**：落败（本批）——WS 信任栅栏 + saucer 无 WebExtension（见 Problem），需改 saucer/fork 或 dsh 上游栅栏配置化才完整，列为远期独立项。
- **WebView 直连 dsh origin**：落败——mac cookie 墙（loopback ADR 实测），且失去 holder/铸币/SSE 隧道整套代理职责。
- **dsh 上游服务端持久化 current selection**：落败——改全员共享产品行为；origin 稳定后浏览器侧本就正常。
- **代理端口也走「每次 OS 分配 + 页面侧跨 origin 迁移会话选择」**：落败——迁移需页面脚本读旧 origin 存储，跨 origin 隔离本就不可达，等于回到问题原点。

## Consequences

- 恢复链闭合：代理 origin 稳定 → `dsh.sessions.current` 存活 → `restoreSelection` 命中 → 上次会话恢复；storage/ 不再按次累积孤儿 origin 目录。注意恢复命中后的空白会话复用（上游 `reuseBlank`）也走 `session/create` API——日志判据是 create body 携带已存 `sessionId`，非「list 后无 create」。
- 安全面无新增暴露：壳只导航到自己 bind 成功的 origin；预占记忆端口的进程最多造成单次恢复失效（新端口随即被记住），dsh 信任栅栏、`Set-Cookie` 永不回页面、铸币模型零改动。
- 行为测试镜像 `.dsh-web-port` 范式：冷启动复用（值不变不写盘）、被占漂移并重新持久化、无记忆/损坏回退 OS 分配并持久化、TIME_WAIT 重绑（非 Windows）；`DshLoopbackProxyTests`/`HarnessRuntimeHostTests` 各接一批。
