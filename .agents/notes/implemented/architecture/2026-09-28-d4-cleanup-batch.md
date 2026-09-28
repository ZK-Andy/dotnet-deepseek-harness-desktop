# Agent Note: D4 清理批——升级判据去重、死配置与无主覆盖退役

Status: implemented

Review: LIGHT/2026-09-28#4/R2=ok（2 轮：首轮 1 Blocker+1 Suggestion，验轮闭合）

## Problem

审计（`.plan/整改方案-三平台震荡后结构清理-2026-09-27.md` §4.4/§4.5）确认三处低风险腐化：升级判据双写（`DshLoopbackProxy` 内 `IsUpgrade` 与 `RelayAsync` 隧道分支同判据各写一份，dsh 网关口径漂移时只改一侧即分叉）、appsettings `Logging` 节零读取者（`src/` 内 `GetSection`/`IConfiguration`/`ConfigurationBuilder` 零命中）、`DSH_DESKTOP_NODE_GLOBAL_PREFIX` 环境覆盖无任何记载与消费者（docs/README/ADR 均无、测试不设、appsettings 无该键）。

## Decision

1. **升级判据收口**：`RelayAsync` 隧道分支改为调用 `IsUpgrade(req)`（判据唯一家，XML doc 记明收口）；行为等价（同一 `TryGetValue` + 非空判定，取值经已确认存在的键索引）。
2. **删 appsettings `Logging` 节**：零读取者，删除后 `Update`/`Runtime` 节消费不变（配置装载是 Home-grown 解析，不读该节）。
3. **退役 `DSH_DESKTOP_NODE_GLOBAL_PREFIX` 环境覆盖**：`ResolveNodeGlobalPrefix` 收敛为 `RuntimeBootstrapOptions.NodeGlobalPrefix` > 默认值两段；`RuntimeBootstrapOptions.NodeGlobalPrefix` 属性**保留**——它是测试注入前缀行为的缝（四处夹具在用），「运行时恒空」由 options 默认构造保证，属配置模型的程序化入口而非死属性。
4. **裁定不动的两项**（审计高/中置信候选，随批记录理由）：`UpdateHttpClient`（单调用点但属 Infrastructure 边界——内联进 Presentation 协调器将违反 D005 的 `new HttpClient` 非边界禁令，待 D3-b 协调器迁 Infrastructure 后随批归位）；`CliShimFile` 降 file-local（`IReadOnlyList<CliShimFile>` 出现在容器 record 的属性上，降级牵连容器可见性，收益为零——保留现状）。

## Alternatives considered

- **`IsUpgrade` 提为 `ShellProxyFraming` 静态**：判据消费者只有 `DshLoopbackProxy` 一类，提升是过度设计；私有静态唯一家即可。落败。
- **`NodeGlobalPrefix` 属性一并删除**：删属性即拆测试缝，四处夹具改走 env 注入——env 覆盖正是本批要退役的形态。落败。
- **`Logging` 节保留作「标准占位」**：零读者的占位就是死配置（本仓曾为「死门/死配置」付过门禁诚实化整批成本）。落败。

## Consequences

- `DSH_DESKTOP_NODE_GLOBAL_PREFIX` 自本批起为未定义行为（进程内无读取点）：如有外部用户依赖此覆盖，升级即失效——按「无记载无消费者」审计口径视为可安全退役。
- `IsUpgrade` 单家后，升级隧道与普通转发面的判据漂移面收为零。
- 挂账：`UpdateHttpClient` 归位随 D3-b（UpdateCoordinator 迁 Infrastructure）；`ProxyHeaderPolicy` 去重（§4.4 第二项，安全不变量双写）涉及页源零透传判据，独立小批处理不并入本批。

## Testing

- `dotnet test` 860/860 全绿；build 0 警告；format 0 差异；code-health/code-conventions/compose-root 门禁绿。
- 升级判据路径由 `DshLoopbackProxyTests` 既有「未铸币/升级 502」与隧道夹具覆盖（行为等价依据：判据表达式逐字相同）。

## Related

- 总纲：`.plan/整改方案-三平台震荡后结构清理-2026-09-27.md` §4.3–4.5（本批为其 D4 的低风险子集；`_webUrl` 与代理值流已随 D1 落地）。
