# Agent Note: post-restructure-ledger-batch（结构整改收官后的挂账清账批）

Status: implemented

Review: FULL/2026-09-29/R1=ok R2=ok R3=ok

## Problem

结构整改总纲全部批次交付后，四笔挂账待清偿，账面与代码/笔记现状不同步：

1. **组合根残留一处 dev 域 env 散读**：`DesktopBootstrap.App.cs` 的 `ConfigureOptions` 回调内直接
   `Environment.GetEnvironmentVariable("DSH_DEVTOOLS")`，是组合根上唯一的 dev 域 env 直读——与
   `docs/architecture.md`「dev 标记由 `Infrastructure/LaunchOptions.Resolve` 单点解析」的陈述有张力
   （余批 3 R1 挂账）。
2. **LauncherActivation 公开原语宽于消费面**：`SocketPath`/`FallbackUidSuffix` 的生产消费在余批 3
   锁地址解析下沉后仅剩同类内（`ResolveInstanceSocketPath`），却仍挂 `public`——公共 API 面宽于需要
   （余批 3 R1 挂账）。
3. **账面/归档遗留**：总纲 ADR 已随收官批改写 implemented 但 HANDOFF 待办区主条目未勾销；
   [2026-09-14-nav-partial-import-order-fix](../../archived/process/2026-09-14-nav-partial-import-order-fix.md)
   笔记的主题文件（`DesktopBootstrap.Navigation.cs`）已随 D1 批删除，按 D1 批 R3 评审挂账转归档。
   不在本批范围的条件挂账另行明示：F 批 R2 的「路由后台腿行为钉」、E 批的「架构图重生成」与
   Erratum 存量清理。

## Decision

三件随一个 FULL 批清账（1、2 为代码面，3 为账面；合并只付一次三审）：

1. **DevTools 判定下沉 LaunchOptions**：`LaunchOptions` 增 `DevToolsEnv` 常量与 `DevTools` bool 值，
   `Resolve` 单点读 env；组合根 `ConfigureOptions` 改读 `preflight.Launch.DevTools`，行为不变。
   env 读取时点由 Build 回调提前到 Preflight 解析——启动链上两者之间无该变量的写者，判定值不变
   （【推断 · 未证】：全仓 grep 无 `DSH_DEVTOOLS` 写点，钉回归锁值；负向存在性主张不可穷证）。
2. **公开原语降 internal**：`SocketPath`/`FallbackUidSuffix` 改 `internal`；测试经既有
   `InternalsVisibleTo`（Infrastructure → Infrastructure.Tests）保持可达，测试零改动。
3. **账面收口**：HANDOFF 待办区勾销上述四条（1、2 为本批交付，总纲主条目因总纲已 implemented
   仅勾销，归档候选条目随第 4 件勾销）；nav-partial 笔记按归档判据移入 `archived/process/`（一次性
   process 历史、主题文件已删、无指引价值，归档时只插 `Archived:` 行），`compose-root-form-separation`
   ADR Consequences 的挂账指针改指 archived 位置。

Erratum 存量清理（`verify-adr-format.py --facts` 29 条 warning，E 批挂账）**不随本批**：与本批代码面
不同构，逐条勘误会使评审 diff 面失焦，拆出随下一批走。

## Alternatives considered

- **保留根上散读、改 architecture.md 陈述为「单点 + 一处例外」**：落败：文档陈述应收敛于代码事实；
  为一处例外软化单点陈述是漂移温床，且下沉成本与改陈述相当而方向相反（收窄而非登记例外）。
- **`SocketPath` 保留 public 以备未来外部消费**：落败：YAGNI——当前零外部消费者；公共面收窄方向
  错误的成本（再放开是加法）远低于宽面泄漏（收不回）。
- **测试改走 public 面而非 IIV**：落败：IIV 已是本仓既有收窄机制（Core/Infrastructure 均授予测试
  程序集），为两条原语保留 public 面恰是本批要消除的。
- **Erratum 29 条随本批合并**：落败：29 条逐条核对引用零命中原因属独立工作单元，混入使本批 diff
  从「两文件代码 + 账面」膨胀为横跨 20+ 笔记，违反「范围 = 相干子集」评审契约。

## Consequences

- 组合根零 dev 域 env 散读，`docs/architecture.md`「`LaunchOptions.Resolve` 单点解析」陈述与代码一致。
- `LauncherActivation` 公共面收窄 2 项；单实例仲裁外部契约仅剩 `ResolveInstanceSocketPath`/
  `TryBindPrimary`/`NotifyPrimary`/`PrimaryListener`（协议常量 `ShowCommand`/`AckResponse` 另计，
  按「协议常量保持固定」规则维持 public）；剩余公共面收窄候选挂账 HANDOFF。
- HANDOFF 待办区清账 4 条；nav-partial 笔记入冷归档，`--facts` 存量 29 条 warning 维持原量随下批。
- 测试面：LaunchOptions 新增 DevTools 钉回归（null/0/1/true 四形态 + 与 dev 判定独立性钉，
  两 dev 触发器均中和）；既有 LauncherActivationTests 零改动。

## Risks

- env 读取时点前移在理论上可被「Preflight 与 Build 之间写 `DSH_DEVTOOLS`」的外部形态改变结果——
  启动链为单一进程内同步序列，无此类写者；若未来引入热改 env 的能力需重新审视该假设。
- 归档移动使指向旧路径的相对链接失效——同批改写 `compose-root-form-separation` 指针，md-links 门禁
  兜底。
