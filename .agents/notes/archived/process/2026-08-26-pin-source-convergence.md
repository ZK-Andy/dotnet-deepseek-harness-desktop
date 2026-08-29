# Agent Note: pin-source-convergence

Status: implemented
Archived: 2026-08-29

## Problem

同一份版本号抄写多处：`bundle-runtime-ci.sh` 默认值（正典）+ 三个 `package-*.yml` 手抄 env + C# `RuntimeVersionGate.MinimumVersion` 常量 + docs 散文。每次 bump 要同步 4–6 处，半 bump（只改一半副本）无人当时拦截——freshness 巡检（ADR freshness-pin-patrol）的内部一致性核对只能事后报警，该暂缓项本批收口。

## Decision

1. **打包链单一来源 = 脚本默认值**：三个 workflow 删除手抄 `DSH_VERSION`/`NODE_VERSION` env，改为 checkout 后「解析钉版正典」步骤经 `check-pin-freshness.sh --print dsh|node` 提取（纯解析不联网、fail loud：提取失败非零退出让 runner 立即红）；闭包缓存 key 消费 `steps.pins.outputs.*`。打包脚本不再被 env 覆盖，回落到同一内置默认值——构建行为与缓存键同源。
2. **缓存键零迁移成本**：env 值本就与脚本默认值逐字节相同，key 字符串不变，既有 actions/cache 全部继续命中。
3. **不可安全生成的两处保持人工同步**：C# 常量（编译期内联）与 docs 散文；由巡检一致性核对兜底拦截漂移。pnpm 主版本线（`version: 11`、node-version: '24'）属刻意宽松约束，不并入精确钉版面。

## Alternatives considered

- **保留手抄 env + 靠巡检事后拦截**：落败——检测发生在 bump 之后而非写入时刻，半 bump 窗口期 CI 会以错误版本出包。
- **反向生成（workflow 为源，渲染进脚本）**：落败——脚本是本地与 CI 共用的运行时真身，生成方向只能是脚本→workflow，倒置会让本地构建依赖渲染步骤。
- **顺手收敛 pnpm/node 主版本约束**：落败——语义不同（浮动主版本是兼容区间不是钉版），混入会模糊「精确钉版」集合的边界。

## Consequences

- 打包流水线新增对 `scripts/check-pin-freshness.sh` 的执行依赖：checkout 后立即可用、无网络需求；脚本自身有离线自测覆盖 `--print` 三键。
- bump 流程简化为：改脚本默认值（+C# 常量/docs 按需）→ 提交；workflow 无须再动。

## Related

- [freshness-pin-patrol](2026-08-26-freshness-pin-patrol.md)：巡检与一致性核对的出处，本决策是其暂缓项的落地；`--print` 复用其提取逻辑。
