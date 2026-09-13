# Agent Note: 首启引导完成导航 401——WebKitGTK 跨 scheme 发起链上 SameSite=Strict 会话 cookie 不随行

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

无 dsh 机器的首启引导完成后，壳从自定义 scheme 占位页（`ryn://app`，wwwroot 引导页）`NavigateAsync` 到 dsh 主界面 URL（`http://127.0.0.1:<port>/?token=…`），用户看到 dsh 的 401 文本页，首启必现（alpha.2 token 鉴权落地后该路径从未复验过）。

沙箱取证（2026-09-14，debug 构建 + 隔离 HOME，dsh `client-connection` 服务端打点）：导航请求**带完整 token 到达**（`authorizeIndex` 铸 cookie 命中 `[mint] token-match=yes`），紧随其后的 303 回环重定向 `GET /` 却**无鉴权 401**。dsh 的会话 cookie 是 `HttpOnly; SameSite=Strict`（`client-connection` `sessionCookie`）：从 `ryn://app` 发起的跨 scheme 导航链被 WebKitGTK 按跨站处理，Strict cookie 不随 303 重定向请求发送 → 铸了也用不上。正常路径（dsh 已就绪时首加载即 http，发起方同站）不受影响。

2026-09-13 轮的「跨 scheme 丢 query」假设被本轮证据修正：query 到达了、丢的是 cookie 随行；但旧假设下的修复方向（先落 http 页再二次导航）恰好同样覆盖——两跳后第二跳是 http 同站链路，cookie 随行、query 也保真。

## Decision

引导完成的导航改**两跳**（`DesktopBootstrap.Lifecycle` `RunBootstrapTaskAsync`）：

1. 第一跳 `NavigateAsync` 到裸 origin（`http://127.0.0.1:<port>/`，无 token）——脱离 `ryn://app` 发起链路；该跳必得 401，瞬时无害（服务端 401 带 `no-store`，无缓存放大）。
2. 等第一跳**真正提交**：`RynNavigationCallbacks.SetOnNavigated`（`WebViewNavigated` 到达信号）+ 5s 超时。必须等——实测 `NavigateAsync` 连发会被 WebKitGTK 合并成一次导航（第二轮实锤：300ms 间隔仍只跑出带 token 的单跳，`[mint]` 先于 `[401]`）。
3. 第二跳带全 token `NavigateAsync(url)`——从 http 页发起的同站导航，Strict cookie 随 303 正常发送，铸用闭环。

沙箱端到端复验（2026-09-14 第三轮）：两次「导航已到达」记录齐全，用户直接进主界面，全程无 401 页。612/612 测试绿。

## Alternatives considered

- **导航后检 401 再重试**（原修复方向一）：落败：401 文本页上 `EvaluateJavaScriptAsync` 30s 超时（2026-09-13 实锤），页面态探测不可靠；且首跳已铸废一个 cookie，路径不如两跳确定。
- **占位页改 http loopback 中转**：落败：占位页靠 `window.__ryn.invoke` 与宿主通信，改 http 承载要另起一套命令通道/端口生命周期，改动面远超缺陷本身。
- **上游修（dsh 放宽 SameSite / Ryn 修跨 scheme 导航链 cookie 语义）**：落败：cookie 形状是 dsh 的安全设计（Strict 是正确默认），不该为壳的引导路径放宽；WebKitGTK 侧属引擎行为，修复周期不可控。壳侧两跳是零依赖止血；若上游日后接管（如 dsh 提供免 cookie 的首载握手），按退役条件删除两跳。
- **接受首启后手动重启一次的降级路径**：落败：2026-09-13 备选中的无奈项，两跳修复成本极低且已验证，无需降级。
- **窗口重建**：落败：为一次导航重建整个 Ryn 窗口，代价与复杂度显著高于两跳。

## Consequences

- 收益：无 dsh 机器首启引导后直达主界面，alpha.2 鉴权落地后该路径首次全通。
- 代价：引导完成导航多一跳（一次瞬时 401 请求 + 等待一次提交信号），首启路径增加数秒内的一次往返；第二跳依赖 `WebViewNavigated` 信号可用性（信号丢失由 5s 超时兜底——降级为原单跳行为，不比修复前更差）。
- 边界：只覆盖引导完成这一次从 `ryn://app` 出发的导航；其余壳侧导航（崩溃恢复/端口漂移）发起方本就是 http 页，不受影响。
- 观测：沙箱取证打点在 dsh 安装副本上（非本仓产物），随沙箱销毁；dsh spawn 先于打点补丁 7s 的竞态让第三轮无服务端日志——修复的行为证据（两次到达 + 直接进主界面）以本 ADR 记录为准。
