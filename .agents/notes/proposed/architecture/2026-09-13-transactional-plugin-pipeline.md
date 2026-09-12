# Agent Note: adopt-transactional-plugin-pipeline

Status: proposed

## Problem

壳侧插件变更（市场引导安装 `EnsureMarketFromRegistryAsync`、随包安装 `EnsureBundledPluginsBeforeSpawnAsync`）都直接对 **active 桌面 profile** 跑 `dsh plugin add`：pnpm 在原目录改写 node_modules/lockfile/package.json，中途崩溃或磁盘满即留下半变更态（依赖树缺肢、lockfile 与 package.json 不一致）。现有三道防线都是事后补丁而非结构消除：`ReconcileProfile` 清不可解析引用、`DesktopProfileBootstrap.InitialBundles` 兜底补回 web-app、`PluginInstallProbe` 装后探针（ADR plugin-install-health-probe）验的是已变更的 active profile——它只能发现损伤，不能防止损伤发生。

官方 desktop（`project-manager.ts`，素材 `.cache/upstream-harness/`）对同一问题的解法是结构性的事务管线：staging profile 上施加变更 → staged 体检 → 带 journal（prepared→active-moved→staging-activated）的 rename 换入 → 启动时 recover() 重放/回滚。active 目录任何时刻要么是旧完整态要么是新完整态。

## Proposal

引入 `PluginProfileTransaction`（新服务，组合根装配），把两个安装驱动的「对 active 跑 plugin add」改为：

1. **staging 准备**：整目录拷贝 active 桌面 profile 到 `profiles/.staging-<uuid>`（拷 pnpm-workspace/package.json/lockfile/插件树；strip 大体积可重建产物可后置优化，首版整拷）。
2. **staged 变更**：`dsh plugin add` 以 `DSH_HOME` 指向临时 home、`--profile` 指向 staging 副本（`RunPluginAddAsync` 的 psi 构造面复用，仅 home/profile 参数化）；spec 形状校验与 minReleaseAge 放宽重试逻辑不变。
3. **staged 体检**：现 `PluginInstallProbe` 的探针目标从 active 改为 staging（`BuildProbePsi` 的 profile/home 参数化）；体检通过才进入激活。
4. **日志化激活**：写 `profiles/.pending.json`（schemaVersion、staging 名、step），随后两步 rename：active→`profiles/.rollback-<uuid>`、staging→active，删 pending。任一步中断，启动时 recover：读 pending，按 step 重放或 rename rollback 回 active；journal 损坏 fail loud（不静默清理）。
5. **回滚面**：激活后主 spawn 首次健康检查失败时删新 active、rename rollback 回来（对照官方 activate 失败回滚）。
6. **退役条件**：探针 ADR（plugin-install-health-probe）的「reconcile 重试」分支并入事务回滚后，其独立 reconcile 分支退役。

迁移路径：先立本 ADR 拍板 → 实施（牵动 `MarketInstallHelper` 两驱动、`PluginInstallProbe`、`DesktopProfileBootstrap.ReconcileProfile` 与启动 recover 钩子、迁移链路）→ 实机验收「安装中途 kill -9 壳进程，重启后 profile 要么旧态要么新态、无半变更」。

开放问题：①staging 期间与正被守护的运行中 dsh 的并发写安全（首版约定：事务仅在 spawn 前窗口执行，运行中不装——与现状一致）；②profile 目录体积与拷贝耗时上探针日志（>500MB 再优化增量拷贝）。

## Alternatives considered

- **维持现状（探针 + reconcile 事后补丁）**：已落地且 596/596 绿。落败原因：探针验的是已损伤面，只能自愈不能消除半变更态；中断窗口内用户可遭遇「装完起不来」，官方已给出结构性解法且我们 2026-09-09 的 profile 迁移原子 rename 已是该思想单步版。
- **lockfile 级回滚（变更前备份 package.json/pnpm-lock，失败时 restore + 重装）**：改动小。落败原因：node_modules 与 lockfile 的不一致仍存在（restore 文本不等于还原依赖树），回滚后仍需全量 reinstall——失败路径反而比 staging 慢；且 pnpm 中断损伤的不止 lockfile。
- **上游承接（给 dsh 加事务化 plugin add）**：落败原因：deepseek-harness 本体不接受外部 PR/issue（2026-09-09 用户确认），跟版缺陷只能壳侧消化（先例 simple-shell-single-global-dsh）。
- **只做 staged 体检不换入（探针指向临时克隆，通过后仍对 active 装）**：落败原因：体检对象与变更对象分离，「克隆上能活」证明不了 active 上的同次变更能活，事务性并未获得。

## Acceptance criteria

- 安装中途 `kill -9` 壳进程后重启：桌面 profile 要么是旧完整态要么是新完整态，host.log 出现 recover 判定行（重放或回滚），无「依赖树缺肢」形态。
- 探针失败时新 active 从未被 dsh 正式消费过——回滚后第一次启动用的就是旧态，不再依赖 ReconcileProfile 修复损伤。
- 事务 journal 损坏时启动 fail loud（响亮日志 + 明确错误态），不静默跳过。
- 既有测试基线全绿 + 新增：中断注入用例（staging 写入中断 / 两步 rename 之间中断 / journal 损坏）与 staging 探针指向断言。

## Risks

- **拷贝成本**：profile 目录大时首启引导变慢（随包 + 市场两驱动叠加）；已知放弃项——首版不做增量/硬链接优化，靠可观测（耗时日志）+ 后置条件触发。
- **rename 原子性平台差**：同目录 rename 三平台均原子，但两步 rename 之间必有窗口——由 pending journal 兜住，这是设计内风险不是缺陷。
- **牵动面广**：MarketInstallHelper / ReconcileProfile / 迁移链路三处既有设计同时改；实施须拆批次（先市场驱动后随包驱动），每批全量回归。
- **与 online-first 种子退役排期交叠**：随包 tgz 种子机制已排期退役，实施时避免为退役中的机制加事务成本——实施前先核该排期状态。
