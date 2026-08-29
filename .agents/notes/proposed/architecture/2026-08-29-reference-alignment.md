# Agent Note: reference-alignment（参照项目对齐——插件生命周期 + 引导健壮性）

Status: proposed

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

2026-08-29 对照参照项目 `dsh-tauri-desk/deepseek-harness-desktop`（Rust/Tauri/React，1381★，v0.9.4）二次审核，确认我方在 batches 一~四（online-first）落地后仍有 5 项与参照的机制/健壮性/UX 不齐：

1. **companion（internal 类插件）仍未 spawn 前安装**——Program.cs 的随包插件装配走「dsh 起来后 后台 3s 装 → `host.Stop()` 重启 dsh → 导航」，而参照 `launch.rs` 明确**所有插件（含 internal）在 spawn dsh 前 ensure 就位、绝不安装后重启**（其 #177 时序原则）。效果 = 首启要两次启动 dsh（第一次无 companion，装完重启加载）。
2. **首启插件引导 UX**——参照有 `preinstall-setup` 引导页（推荐/修复/跳过 chip + 日志回流 + 取消事件）；我方首启只自动装 dshmarket，无用户选择/确认面与日志回流。
3. **下载健壮性**——参照 `download/` 有断点续传（Range）+ 多源镜像回落（官方→ghfast.top）+ 原子 staging/backup 切换 + Windows 文件锁重试；我方 `RuntimeBootstrap` 仅 `GetAsync→CopyTo` 单源 + SHA256 校验 + 重试，无上述保障。
4. **CLI shim / PATH 注册**——参照 `service/cli/shim/` 安装后把 `dsh`/`pnpm` 注入用户 PATH（Windows `%LOCALAPPDATA%\bin` shim + HKCU Path + `WM_SETTINGCHANGE`；mac/linux `~/.local/bin` + shell rc 幂等块）；我方无此能力。
5. **boot 假活看门狗**——参照 `desktop/plugin_boot.rs` 对「卡 Loading plugins」做有界恢复（有界刷新门控）；我方 `PageHealthMonitor` 只观测（阶段 1）不自动恢复。

有意设计差异（**不**在本 ADR 对齐）：下载模型（参照 zip 发行版 vs 我方 npm @latest）、端口（固定+自愈 vs `--port 0`）、自更新触发（参照 10min 轮询+toast vs 我方不轮询）、macOS 自更新（双方均手动 dmg）、迁移（参照幂等 migrate vs 我方有意不做）。前端栈（React SPA + iframe vs 我方 C#/Ryn）**延后专项讨论**，不在本 ADR 范围。

## Proposal

按参照机制对齐 5 项（排序即实施批次），全部遵守既有 ADR 与质量门。

### 批次一 · companion 改 spawn 前安装（internal 时序对齐）✅

- 把「随包插件装配」从 dsh 起来后的后台 3s 任务，迁到 **spawn dsh 之前**：确定运行时（bundled/PATH-dsh 走 `host.StartAsync` 前；引导路径在 `BindRuntime` + `EnsureMarketFromRegistry` 后、`StartAsync` 前）→ `AssemblePending` → 有 pending 即 `plugin add` → 校验/补写 bundles → 再 `StartAsync`。
- 消除首启「先起 dsh 无 companion → 装 → 重启」的二次启动；首启 dsh 第一次即带 full 插件集。
- 保留 dev 隔离守卫（显式覆盖 home 时跳过）；`restartTriggered`/恢复覆写路径随之退役（不再需要安装后重启）。
- 失败语义：companion 安装 best-effort 仍只告警不阻断（缺 companion 不阻 dsh 起动），但改为 spawn 前尝试、失败留痕。
- ✅ 批次一已落地（2026-08-29，提交 `cfa4113`）：`MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync` + 双路径 spawn 前接线，测试 389/389、覆盖率 52.3%。

### 批次二 · 首启插件引导页（preinstall UX 对齐）

- 静态 wwwroot 引导页增「插件引导」步：安装 dshmarket（与任何随包插件）前，显示推荐/可选插件清单（chip）+ 确认/跳过 + 安装日志回流（进度页持续显示 `plugin add` 的 stderr/stdout）。
- 状态机在既有 `BootstrapStep` 内增一个 `PreinstallPlugins`/由引导页交互驱动：引导页经 IPC 发「确认装」/「跳过」→ 宿主执行 dshmarket 安装并回传日志；跳过则该次不装（less-bootstrapped 但有重试入口）。
- 与批次一的 spawn 前安装合流：插件引导页确认后、`StartAsync` 前安装。

### 批次三 · 下载健壮性（download 对齐）

- `RuntimeBootstrap` 下载：改用**断点续传**（`Range` 头 + 已下临时文件续传）、**多源回落**（官方 nodejs.org dist 主源 + 镜像后备；镜像仅用于「有可信 SHA256 摘要」场景，防投毒）、**原子 staging**（下载+解压先落同盘临时目录，成功 rename 为正式目录保留 `runtimeDir`，失败清理恢复）与 **Windows 文件锁重试**（rename/remove 有界重试）。
- 摘要仍来自 `SHASUMS256.txt`（既有）；下载临时文件与正式目录分离，失败不残留半成品。

### 批次四 · CLI shim / PATH 注册（三个平台）

- 安装成功后（或每次启动对账）注册 `dsh`/`pnpm` shim：Windows `%LOCALAPPDATA%\deepseek-harness\bin` 生成 `.cmd`/`.ps1` + `HKCU\Environment\Path` 幂等追加 + `WM_SETTINGCHANGE` 广播；mac/linux `~/.local/bin` 落 shim + `.zshrc`/`.bashrc` 幂等更新块。
- shim 优先本地兼容 node、回退捆绑运行时；pnpm shim 优先用户自有 pnpm。幂等合并、绝不覆盖用户配置；写入前说明路径（workflow 边界），失败仅告警不阻启动。
- 桌面壳只在自己安装/首次登录负责注册，避免与用户已有 PATH 冲突。

### 批次五 · boot 假活看门狗（PageHealthMonitor 有界恢复）

- `PageHealthMonitor` 从「只观测」升级为「观测 + 有界恢复」：连续 Dead 达阈值后，先一次有界刷新（`NavigateAsync` 当前 origin）或触发一次有界重载；**有界**——恢复计数达上限即停止并 leave 观测面（防误报重启循环），恢复计数窗口随同成功复位。
- 对齐参照 `plugin_boot.rs` 的「卡 Loading plugins」恢复：探测到「dsh 在跑但页面空白」时，做有界 reload 而非无限轮询。

## Alternatives considered

- **companion 维持在安装后重启**：功能可用，但违背参照「绝不安装后重启」原则，且首启两次启动 dsh 引入会话/端口二次初始化抖动。落败。
- **引入 React + Vite 换型前端**（完全等同参照）：技术栈等同让 UI/状态机逐点对齐成本最低，但引入前端构建链 + iframe 桥 + 容器化,与我方"C#/Ryn 稳定 + 静态 wwwroot + companion 插件"的既有稳定面冲突，属大换型。**延后专项讨论**（用户批示），本 ADR 先做能力等价（引导页增强）。
- **下载层多源镜像无摘要也镜像**：镜像只信任"有可信摘要"场景，否则第三方镜像投毒风险。落败。
- **CLI shim 写入系统 PATH（HKCU vs HKLM）**：只写用户级 HKCU + `~/.local/bin`，不触系统级；系统级需管理员且影响面大。落败。
- **假活看门狗无限重载**：误报会形成重启/重载循环，伤害可用性。落败；有界恢复 + 计数上限。

## Consequences

- 首启：dsh 第一次启动即带 full 插件集（companion + dshmarket），无「装后重启」；引导页提供插件确认/跳过与日志回流。
- 下载：断网/弱网更稳（断点续传 + 镜像回落 + 原子落盘），失败不残留半成品，重试可续传。
- CLI：安装后用户可在终端直接用 `dsh`/`pnpm` 命令（环境生态一等公民的又一落点）。
- 假活：白屏自愈有界化，避免误报重启循环。
- 测试：每项配套回归（时序/状态机/下载重试/镜像 / shim 路径 / 有界恢复）；行为级变更走三重审核。
- 每批独立提交 + 门禁 + CI。

## Related

- [online-first-unbundled-runtime](../../implemented/architecture/2026-08-29-online-first-unbundled-runtime.md)（implemented）：本 ADR 在其首启引导/下载层之上对齐健壮性与插件时序；下载层改动直接作用于其 `RuntimeBootstrap`。
- [plugin-surface-consolidation](../../implemented/feature/2026-08-29-plugin-surface-consolidation.md)（implemented）：本 ADR 批次一/二在其「dshmarket 迁 spawn 前」基础上把 companion（internal）也收敛到 spawn 前，并补插件引导 UX。
- [shell-observability-diagnostics](../../implemented/architecture/2026-08-24-shell-observability-diagnostics.md)（implemented）：PageHealthMonitor 从只观测升级为有界恢复，观测面保留。
- [desktop-shell-companion-plugin](../../implemented/process/2026-08-21-desktop-shell-companion-plugin.md)（implemented）：companion 供给链与装配，本 ADR 批次一调整其装配时点。
- 参照项目 `dsh-tauri-desk/deepseek-harness-desktop`（v0.9.4，调研详录 `.plan/journal/2026-08-29-reference-project-key-differences.md`）。
