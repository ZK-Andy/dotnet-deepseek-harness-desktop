# Agent Note: plugin-surface-consolidation（插件面收口）

Status: implemented

## Problem

online-first 去捆绑转向（`online-first-unbundled-runtime`）批次二已把安装器瘦身、闭包退役、dshmarket 无本地来源回退 registry 落地。但插件面仍残留半捆绑时代的机器：`BundledPluginCatalog` 仍是「dshmarket + companion」两条目，market 解析器与 companion 解析器各带运行时目录 tgz/目录近死分支（其唯一生成器 `bundle-runtime-ci.sh` 已删），`AssemblePending` 的 registry 归化/分组机只为随包 dshmarket 一个消费者存在；且退役 dshmarket 种子时，若存量 profile 残留指向已消失 tgz 的 `file:` 引用，会构成不可解析 bundle 引用（对照 dsh-tauri-desk #177 事故），必须在启动前 reconcile。

## Decision

批次三把插件面收口到 online-first 终态——**随包仅 companion（安装器资源供给）**，dshmarket 改由首启引导经 registry 安装（新装 + 存量 seed 自愈归化），彻底删除半捆绑时代的近死分支与归化机器：

- **目录收缩**：`BundledPluginCatalog.All` 收缩为单条目 `dsh-desktop-companion`（`ResolveCompanionSpec`），dshmarket 条目随其 registry 回退形态一并退役。
- **解析器近死分支退役**：删 `MarketInstallHelper.ResolveMarketSpec`/`MarketRegistrySpec`（其运行时目录 tgz/目录分支唯一生成器已删，只剩 registry 回退——该回退移入引导后无处消费）；`ResolveCompanionSpec` 删运行时目录 tgz/目录回退（批次二已迁入安装器 `resources/plugins`，运行时目录分支为近死）。companion 安装器资源缺失时返回 null（开发用 PATH dsh 由调用方跳过）。
- **归化/分组机器退役**：`AssemblePending` 删 `NormalizeToRegistry`/`IsLocalSpec`/`IsPathSpec`/`ReadDependencySpec`/`FromRegistry` 分支——这些只服务随包 dshmarket 一个消费者；companion 无语义（无 registry 上游、无 seed 归化）。消费点 `Program.cs` 随之去掉本地/registry 双组 spawn，单组安装。
- **dshmarket 迁入引导**：`RuntimeBootstrap` 在 `InstallDsh` 步骤后追加一处 registry 安装——经随 Node 的 npm 以 `dsh plugin add dshmarket@latest`（写 desktop profile）装市场。一次 add 同时承担新装与存量 seed 自愈归化（`dshmarket@latest` 显式 spec 对既存依赖同样强制改写为 registry 形态，等价 `bundled-plugin-registry-normalization` 的归化语义）。此步骤在引导状态机内、dsh spawn **之前**（对齐 dsh-tauri-desk「插件与核心一起就位」，不启动后再装→重启）。
- **config reconcile 先于启动**：`DesktopProfileBootstrap` 新增 reconcile——扫描 desktop profile 的 `dependencies`/`dsh.profile.bundles`，删除解析目标已不存在的本地 `file:`/`link:` 引用（被退役的 dshmarket 种子属之），再启动。幂等、fail loud 于结构错误（记日志不阻断）。此为 #177 事故的对齐约束：不允许不可解析 bundle 引用残留。

## Alternatives considered

- **保留 catalog 双条目 + registry 机制（仍走后台装配装 dshmarket）**：落败——dshmarket 自动安装位改引导后，后台装配的 registry 分支失去唯一消费者，残留半捆绑机器（近死分支 + 仅测试可达的归化/分组逻辑）正是批次三要清理的包袱；且与「插件与核心一起就位」的 launch 时序约束不符。
- **dshmarket 完全消失**（不再自动装，交给 dsh 内置/用户手动装市场）：落败——去捆绑后市场是核心用户体验（装第三方插件全靠它），不自动装是功能回退；ADR「新启形态 §2 registry 安装 dshmarket」明示保留。
- **保留运行时目录分支作 dev 兼容**（companion 仍可从运行时目录 tgz/目录解析）：落败——dev 场景用另一条已就位的路径（仓库内开发运行时用 `DSH_DESKTOP_RUNTIME_DIR`；companion 由安装器资源供给），运行时目录 tgz/目录分支是闭包时代残留，生成器已删，永不活性。
- **reconcile 移到安装任务内**（沿用 `EnsureBundlesContainsAsync` 兜底）：落败——安装任务在 dsh spawn 后（后台、3s 延迟），无法兑现「启动前 reconcile」；且 `EnsureBundlesContainsAsync` 只增不减，删不了不可解析引用。

## Consequences

- 收益：插件面收敛到「随包 = companion + 注册表市场」单一模型；近死分支与归化/分组机器退役，`AssemblePending` 大幅简化；dshmarket 自动安装位从「后台 3s 装→重启 dsh」改为「引导内、dsh spawn 前」——插件与核心一次就位，重启循环消失；compliance 对齐 dsh-tauri-desk #177（启动前 reconcile 不可解析引用）。
- 代价/风险：dshmarket 安装从引导的 registry 路径走，**必须联网**（online-first 面向联网模型场景，可接受；断网时引导在 install 步 fail loud，进度页可重试）；`dshmarket@latest` 不经我方验证矩阵（与用户自装同风险面，市场出现后由 dshmarket 自带更新检查跟进）；companion 安装器资源缺失时无法自动装（仅开发用 PATH dsh 场景，调用方跳过）。
- 引导状态机改动：新增一个 `InstallMarket` 步骤（或并入 `InstallDsh` 之后一步），保持「每步完成即验证产物」约束——dshmarket 装完校验其 `dependencies`/bundles 就位。
- 测试：删 3 个守护测试（`RegistryFallbackSpec_WithoutNormalization_Skipped` / `ResolveCompanionSpec_UsesRuntimeTgz_WhenInstallerDirMissing` / `RealCatalog_ResolvesBothBundledPlugins_FromRuntimeLayout`）；`MarketInstallHelperTests` 删随 `ResolveMarketSpec`/`IsLocalSpec`/`IsPathSpec`/`ReadDependencySpec`/运行时目录分支相关的用例；新增 reconcile 回归 + 引导装 dshmarket 回归（hooks 注入断言）。

## Related

- [online-first-unbundled-runtime](../../proposed/architecture/2026-08-29-online-first-unbundled-runtime.md)（proposed）：本篇为其实施批次三——插件面收口 + config reconcile + dshmarket 迁引导。
- [bundled-plugin-registry-normalization](../feature/2026-08-29-bundled-plugin-registry-normalization.md)（implemented）：本篇退役其「随包 = 种子」前提与后台装配归化路径；归化语义（`dshmarket@latest` 显式改写存量）由引导注册表安装承接。
- [bundled-plugin-version-aware-catalog](../feature/2026-08-25-bundled-plugin-version-aware-catalog.md)（implemented）：清单机制本体保留（companion 版本感知升级不因收口改变）；随包目录收缩后其多插件通用语义不再被消费。
- [desktop-shell-companion-plugin](../process/2026-08-21-desktop-shell-companion-plugin.md)（implemented）：companion 随包供给与安装器资源通道的既有决定，本篇不动。
- [shared-home-desktop-profile](../architecture/2026-08-23-shared-home-desktop-profile.md)（implemented）：desktop profile 自举与 `DesktopProfileBootstrap`，本篇在其上加 reconcile。
