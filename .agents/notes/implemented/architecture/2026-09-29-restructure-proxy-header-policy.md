# Agent Note: restructure-proxy-header-policy（余批 2：§4.4 源头改写策略单源 + 双面行为钉）

Status: implemented

Review: LIGHT/2026-09-29/R2=ok（首轮 0 Blocker 0 Suggestion；两出面等价性经逐字节比对 + 前缀性机制论证核验）

中文（双语暂不启用）。

## Problem

结构整改总纲（`process/2026-09-27-post-packaging-churn-restructure`）§4.4 点名的**安全不变量双写**：Origin/Referer→自源改写策略在代理两个出口面各写一份——

- HttpClient 转发面 `DshShellForward.BuildForwardRequest`：页源头剔除（逐字比对 `origin`/`referer`）+ `Origin→authority`、`Referer→target`；
- 裸 TCP 隧道面 `DshLoopbackTunnel.BuildUpgradeHead`：页源头剔除（另一份逐字比对）+ `Origin→authority`、`Referer→authority+path`，注释自认「与普通转发面同法」。

该不变量是**页源零透传**：页源代理 URL 在 dsh 网关处即外源 403。两份实现意味着 dsh 网关口径漂移时只会改一侧——另一面静默漏页源值（隧道面还是裸 TCP，无 HttpClient 兜底）。§4.4 的另一子项「升级判据双写」已由 D4 批收口（`DshLoopbackProxy.IsUpgrade` 唯一家），本批为 §4.4 收尾。

## Decision

1. **`ProxyHeaderPolicy`（Infrastructure/Runtime，internal static）为源头改写策略唯一事实源**，收两样：
   - `IsPageSourceHeader(name)`——页面侧 Origin/Referer 头名判定（大小写不敏感），两出口面的剔除分支共用；
   - `SelfSource(authority, pathAndQuery)`——自源形态单点：Origin = dsh authority、Referer = authority + 原样 path/query（与 dsh 直出同形态）。
2. **两出口面接单源**：转发面剔除分支换 `IsPageSourceHeader`，改写值换 `SelfSource(authority, target[authority.Length..])`（target 由调用方以同一路由 authority + 页面 path 拼装，`DshLoopbackProxy.cs` 的 `target = authority + req.Target`，GetLeftPart 前缀恒成立）；隧道面同理换用，并升 `internal static` 作测试缝。
3. **双面行为钉回归**（`ProxyHeaderPolicyTests`，4 测）：头名判定大小写变体 + 误伤反例；`SelfSource` 形态；转发面恶意页源头（外源 origin/referer 含 token 值、大小写变体）零透传 + 自源形态；隧道面同款 + Host 必写/sec-fetch-site/cookie 贴壳值/Upgrade 透传。

## Alternatives considered

- **只钉测试不抽单源**：两份实现 + 两份测试也能锁行为，但「口径漂移只改一面」的事故形态不变——测试发现漂移后仍要人肉同步两处。落败——不变量该有一个家。
- **策略下沉 Core**：策略消费的 `StringBuilder`/`HttpRequestMessage` 构造都在 Infrastructure，且无测试解锁增量（Core.Tests 可测的只有纯字符串形态，现测试已在 Infrastructure.Tests 覆盖）。落败——MVP 判据不命中，不为「Core 更纯」搬家。
- **转发面 Referer 保持「target 原样串」不改裁 path**：可做到字面零差异，但形态知识（authority+path）仍留在转发面局部，单源不成立。采纳裁 path 方案并在 ADR 记账理论差异（见 Consequences）。

## Consequences

- dsh 网关口径漂移时只改 `ProxyHeaderPolicy` 一处；两出口面形态由同一函数保证一致，4 条钉回归锁「改一面漏一面」事故。
- 转发面 Referer 从「target 原样串」收敛为「规范 authority + 裁出 path」：对规范形态 target（dsh 铸币恒为 `http://127.0.0.1:高位随机端口`）逐字节等价；理论差异仅当 target 带大写 host/默认端口等非规范形态——此时收敛后恰与隧道面同形，正是本批目的。
- 行为钉测试 +4，基线 870→874。
- 结构整改总纲 §4.4 全部收口；仅余余批 3（#5/#7 + PayloadSmoke 移位，FULL 面）。

## Related

- 总纲：`proposed/process/2026-09-27-post-packaging-churn-restructure`（§4.4 收尾）
- 升级判据收口先例：`implemented/architecture/2026-09-28-d4-cleanup-batch`
- 代理面原始 ADR：`implemented/architecture/2026-09-27-loopback-forward-proxy`
