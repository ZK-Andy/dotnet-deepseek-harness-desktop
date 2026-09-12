# Agent Note: adopt-transactional-plugin-pipeline

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

壳侧插件变更（市场引导安装 `EnsureMarketFromRegistryAsync`、随包安装 `EnsureBundledPluginsBeforeSpawnAsync`）都直接对 **active 桌面 profile** 跑 `dsh plugin add`：pnpm 在原目录改写 node_modules/lockfile/package.json，中途崩溃或磁盘满即留下半变更态（依赖树缺肢、lockfile 与 package.json 不一致）。现有三道防线都是事后补丁而非结构消除：`ReconcileProfile` 清不可解析引用、`DesktopProfileBootstrap.InitialBundles` 兜底补回 web-app、`PluginInstallProbe` 装后探针（ADR plugin-install-health-probe）验的是已变更的 active profile——它只能发现损伤，不能防止损伤发生。

官方 desktop（`project-manager.ts`，素材 `.cache/upstream-harness/`）对同一问题的解法是结构性的事务管线：staging profile 上施加变更 → staged 体检 → 带 journal（prepared→active-moved→staging-activated）的 rename 换入 → 启动时 recover() 重放/回滚。active 目录任何时刻要么是旧完整态要么是新完整态。

## Decision

`PluginProfileTransaction`（`Services/PluginProfileTransaction.cs`）承载事务，两个安装驱动共用：

1. **staging 准备**（`Begin`）：整目录拷贝 active 桌面 profile 到 `<dshHome>/.tx-<uuid>/profiles/<DesktopProfileName>`（排除 `.dsh-web-port`/`.dsh-pid` 运行时管理文件；耗时日志可观测）。active profile 缺失即抛（调用链保证 EnsureProfile 先行，缺序炸出来）。
2. **staged 变更**：`dsh plugin add` 的 `DSH_HOME` 指向 staging home、`--profile` 名不变（`RunPluginAddAsync` 已按 home 参数化）；spec 形状校验与 minReleaseAge 放宽重试逻辑不变；allowBuilds 放行与 bundles 补写都做在 staging 副本上。
3. **staged 体检**：`PluginInstallProbe.BuildProbePsi(stagingHome)` 探针指向 staging，通过才激活；不过 = `Discard()`（staging 作废、active 保持旧完整态）。全失败的随包轮同样 `Discard()`。
4. **日志化激活**（`Activate`）：写 `profiles/.pending.json`（schemaVersion、staging/rollback 路径、step）后两步 rename：active→`profiles/.rollback-<uuid>`、staging→active，删 pending，回收 rollback 与 staging home。
5. **启动 recover**（`Recover`，接在 `EnsureDesktopProfile` 的迁移之后）：Prepared = staging 作废；ActiveMoved = staging 在位则重放换入、缺失则 rollback 回滚、双缺 fail loud；StagingActivated = 只清尾巴；journal 损坏/版本不认识 fail loud（抛异常不静默清理）；无 journal 时清扫 stray `.tx-*`/`.rollback-*`。
6. **退役**：探针 ADR（plugin-install-health-probe）的「对 active 事后探针 + reconcile 重试」分支与两个驱动外的 best-effort 探针收口（`RunInstallProbeBestEffortAsync`）随本管线退役；`ReconcileProfile` 保留其启动前清存量死引用职责（与本管线互补，不重叠）。

open 约定：事务仅在 spawn 前窗口执行（运行中 dsh 不装，与既有约定一致）；profile 拷贝耗时记日志（>500MB 再议增量拷贝）。

## Alternatives considered

- **维持现状（探针 + reconcile 事后补丁）**：已落地且 596/596 绿。落败原因：探针验的是已损伤面，只能自愈不能消除半变更态；中断窗口内用户可遭遇「装完起不来」，官方已给出结构性解法且我们 2026-09-09 的 profile 迁移原子 rename 已是该思想单步版。
- **lockfile 级回滚（变更前备份 package.json/pnpm-lock，失败时 restore + 重装）**：改动小。落败原因：node_modules 与 lockfile 的不一致仍存在（restore 文本不等于还原依赖树），回滚后仍需全量 reinstall——失败路径反而比 staging 慢；且 pnpm 中断损伤的不止 lockfile。
- **上游承接（给 dsh 加事务化 plugin add）**：落败原因：deepseek-harness 本体不接受外部 PR/issue（2026-09-09 用户确认），跟版缺陷只能壳侧消化（先例 simple-shell-single-global-dsh）。
- **只做 staged 体检不换入（探针指向临时克隆，通过后仍对 active 装）**：落败原因：体检对象与变更对象分离，「克隆上能活」证明不了 active 上的同次变更能活，事务性并未获得。

## Consequences

- 收益：active profile 任何时刻要么旧完整态要么新完整态，安装中断从「用户可遭遇的半变更态」降为「下次启动 recover 重放/回滚的日志事件」；staged 探针失败时新 active 从未被 dsh 正式消费——不再依赖 ReconcileProfile 修复损伤。
- 代价：安装轮次整拷 profile 一次（含插件树，秒级，`Begin` 耗时日志可观测；首版不做增量/硬链接优化，条件触发后再议）；两步 rename 之间留有 journal 兜住的窗口（设计内风险，非缺陷——同卷 rename 三平台原子）。
- journal 损坏 fail loud 是刻意取舍：静默清理会把「active 缺位」的窗口态当无事发生；错误信息给出手动恢复路径（删 pending 文件）。
- 与 online-first 种子退役无交叠：随包 tgz 现为安装器资源供给（bundled-plugin-self-install-equivalence），事务对其透明。

## Testing

- 事务单测 13 例（`PluginProfileTransactionTests`）：拷贝排除运行时文件、缺 profile 抛、Activate/Discard 收口、recover 全分支（Prepared 作废 / Prepared+rollback 崩溃窗口回滚 / ActiveMoved 重放 / ActiveMoved 回滚 / ActiveMoved 双缺 fail loud / StagingActivated 尾巴 / stray 清扫）、journal 损坏与版本不认识 fail loud；中断注入 = 手工摆 journal + 目录形态（等价两步 rename 之间被 kill）。
- 驱动测试改断言事务形态（`MarketInstallHelperTests`）：DSH_HOME 指 staging home、换入后 active 清单含新包、安装失败/staged 探针失败时 active 原封不动且无 `.tx-*`/pending 残留（市场与随包两驱动对映覆盖）。
- 探针 psi 形状补 `homeOverride` 指向 staging 的断言；真实 dsh 冒烟仍走 `DSH_TEST_E2E=1` 门控。
- 基线 610/610、0 警告（原 596 − 退役的 VerifyAfterInstallAsync 三例 + 事务/驱动新例）。

## Related

- [插件安装后启动体检探针](2026-09-13-plugin-install-health-probe.md)：staged 体检承接其「换血前先证明能活」，其事后 reconcile 分支退役。
- [子进程环境净化与插件 spec 校验](2026-09-09-spawn-env-and-plugin-spec-hardening.md)：spec 校验与 env 净化在 staging 变更路径上原样生效；启动 recover 接在 profile 迁移（desktop-profile-rename）之后。
