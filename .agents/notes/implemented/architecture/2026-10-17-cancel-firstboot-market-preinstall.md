# Agent Note: 取消第三方插件市场的首启引导安装

Status: implemented

Review: FULL/2026-10-17/R1=ok R2=ok R3=ok

Erratum: 2026-10-17 — 交付形态更正：五片（gross 548/323/296/348/101）按提交语义切分，**仅 `7cb5a32`/`c62f725` 两片的树可编译**——前三片引用了本批已删的类型（`PreinstallChoiceGate`/`PreinstallCommandRouter`/`MarketInstallHelper.IsBundleInstalled`），故「逐片推送则单片 ≤600」不可行。交付改为单次快进推送（`5a4b29e..8940650`），pre-push 增量纪律闸按 [increment-gate-enforce](../process/2026-10-14-increment-gate-enforce.md) Decision 4 的「在 ADR 记理由」通道豁免（体积由删除已退役接线构成，无可拆的质量面）；CI run `38951121337` 的增量闸步（base = `github.event.before`，整段 gross 1647）红、其余作业全绿，且该步不可重跑转绿（base 由事件载荷固定）。随后 bump `9ff22ba` + tag `v0.6.11` 交付。

## Problem

桌面壳在首启引导页呈现「插件准备」步：把第三方插件市场 `dshmarket` 作为推荐 chip 交给用户确认/跳过，确认后经 registry 装进桌面专属 profile（走事务管线换入）；无人值守场景以 `DSH_DESKTOP_PREINSTALL_AUTO=skip` 跳过（ADR [preinstall-unattended-skip](../../archived/feature/2026-09-26-preinstall-unattended-skip.md)）。这一步把「壳代装第三方插件」写进产品面，并牵连整套接线：引导页第 4 步与 chip/日志区、IPC 路由 `desktop.preinstall.choose`、`dsh-desktop-preinstall` 事件帧、专用决策闸门、preset 清单、市场 registry 安装驱动与两个专用配置键。

用户 2026-10-17 决定：取消这一引导安装。市场是上游 registry 面（与用户自装完全等价），壳不必代装；随包插件（`dsh-desktop-companion`）是壳必需品，其 spawn 前静默自愈不在取消范围。

## Decision

1. **删掉首启插件引导相**：`FirstBootBootstrapService.RunPreinstallPhaseAsync` 及其呈现/等待/安装三段与无人值守跳过常量退役；`InstallBootstrapPluginsAsync` 只留随包 companion 自愈。
2. **接线面同批退役**：`PreinstallChoiceGate`/`PreinstallChoice`、`PreinstallCommandRouter`（`desktop.preinstall.choose`）、`PreinstallFrame` 与 `PagePump` 的 preinstall 事件/日志推手、`IFirstBootUi` 的四个 preinstall 成员、`IFirstBootBootstrap.PreinstallGate`、`BootstrapStep.PreinstallPlugins`（引导页步骤 4 → 3）、`PresetPluginCatalog`、`UiCopy` 的 preinstall 词条与引导页 chip 区。
3. **市场安装驱动退役**：`MarketInstallHelper.MarketSpec` 与 `EnsureMarketFromRegistryAsync` 删除；`PluginSpecOrigin` 与 `RunPluginAddAsync` 的 origin 参数及其 registry 收口一并删除——唯一调用方是随包 `file:` spec。`IsValidRegistrySpec` 保留：`RuntimeBootstrap` 仍用它校验 dsh 安装 spec。同批退役两处随本批失去调用方的面：`PluginProcessRunner.RunStreamingAsync`（引导日志回流的唯一用户）与 `MarketInstallHelper.IsBundleInstalled` 委托壳（其六个用例迁至 Core `ProfilePackageCheckTests`，覆盖不降）。
4. **配置面收窄**：`PreinstallChoiceTimeoutMinutes` 退役；`PreinstallPollIntervalMilliseconds` 改名 `RetryPollIntervalMilliseconds`（其消费点自始是引导失败重试的轮询节拍，与插件引导无关）。
5. **市场改由用户自行安装**：按 dsh 的插件安装方式装入桌面 profile（`dsh plugin --profile dotnet-desktop add dshmarket@latest`）；README 双语、user-guide、faq 与 architecture 同批改口径。

## Alternatives considered

- **保留引导相、默认跳过**：落败——仍留 4 步引导页 + 帧/闸门/路由全套接线与「壳代装第三方」语义，只是不装。
- **只在 CI 关掉（沿用跳过变量）**：落败——真人首启仍被引导，问题不在无人值守侧。
- **保留 `EnsureMarketFromRegistryAsync` 以备后用**：落败——无调用方的 registry 安装面即死代码；要恢复可回 git 历史取。
- **保留 `PluginSpecOrigin` 参数**：落败——唯一调用点恒传 `Bundled`，实参化的是永久常量。
- **连 companion 静默自愈一并删掉**：落败——companion 是壳必需品（页面注入/语言/插件能力），非第三方市场。

## Consequences

- 首启引导页只剩运行时三步（Node / dsh / 校验），无 chip、无日志区、无 `desktop.preinstall.choose`；无人值守冒烟脚本的 `DSH_DESKTOP_PREINSTALL_AUTO` 与 docker 透传项同批删除。
- 未装市场的用户需在 dsh 内自行安装（文档已改口径）；桌面 profile 的市场 bundle 补写自愈随安装面一并消失。
- 取代关系：ADR reference-alignment 批次二与 [preinstall-unattended-skip](../../archived/feature/2026-09-26-preinstall-unattended-skip.md) 整体退役（后者归档）。按勘误纪律插 `Erratum:` 行的七篇：spawn-env-and-plugin-spec-hardening（registry 收口落点）、timeout-hardcode-to-appsettings（键清单）、online-first-unbundled-runtime（首启 registry 装市场的落点）、[plugin-surface-consolidation](../../implemented/feature/2026-08-29-plugin-surface-consolidation.md)（其 Alternatives 记为落败的「交给用户手动装」即本批采纳的决定）、transactional-plugin-pipeline 与 plugin-install-health-probe（「两个安装驱动」计数口径）、reference-alignment（批次二整段）。
- 判据：只有「现役落点/现役计数」类断言插勘误；Problem/Alternatives 里对当时符号的历史叙述不动（沿 StartupWiring 改名先例）。
- 测试基线 1083 → 1048（删 4 个专用测试文件与 5 个市场安装用例）。

## Testing

- `dotnet test` 全绿 0 警告：228 + 6 + 217 + 597 = 1048（Core 222→228 为六用例迁家、Infrastructure 603→597 抵消）（`scripts/test-baseline.json` 与 README 双语徽章同批更新）。
- 门禁：ui-copy（index.html 与 UiCopy 双向对账）、code-health、compose-root、registration-discipline、code-conventions、review-tier、readme-badges 全绿；`dotnet format --verify-no-changes --severity warn` 干净；评审为 FULL 三审 + 两轮验轮（首轮 R1 1B+7S、R2 1B+4S、R3 1B+5S 全修毕；验轮 R1/R2 0B，R3 末轮 1B 属记账数字面——量测点已钉在增量行的命令 + 提交树上、算术复算即闭，轮次上限 3 未开第 4 轮，见 ADR review-round-convergence）。
- 增量纪律：本批删除主导（add 210 / del 1406）而 gross 超 600 帽，按 ADR coupling-increment-enforce 决定 4 拆五片提交（gross 548 / 323 / 296 / 348 / 101，片片帽内；逐片推送则单片 ≤600）。聚合面（push / CI 的 `merge-base origin/main HEAD` = `5a4b29e` 起算，含本批之前的两次笔记清理提交）同样超帽，按同条「拆批或在 ADR 记理由」记理由于此——体积由删除已退役接线构成，无可拆的质量面。量测点：本行所在提交树（复算 `git diff ad662f0 <tree> --numstat` = add 210 / del 1406 / gross 1616、54 文件；`git diff 5a4b29e <tree> --numstat` = add 211 / del 1412 / gross 1623、55 文件）——该口径已含本行与 `Review:` 行自身的行数，故钉在「行内命令 + 提交树」上，不随措辞改写飘移。
- 覆盖率口径：本机干净并集（`--results-directory TestResults` + `scripts/coverage-summary.py`，与 CI 同口径；先清空 `tests/*/TestResults` 旧卷避免陈旧 cobertura 混入——曾复现 60.62% 的假读数）64.79% → 64.77%，与基线差 0.02pp（容差 0.5pp 内；本机口径偏高约 4 行，见 [docs/testing.md](../../../../docs/testing.md)）。
- 实机未验（2026-10-17 用户拍板不再验证）：引导页三步序与「进主界面不再出现插件准备步」缺无全局 dsh 的干净环境——本机日志恒为「全局 dsh 可用…跳过首启引导」，引导页未进入过。
