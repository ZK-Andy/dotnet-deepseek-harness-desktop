# Agent Note: reference-alignment（参照项目对齐——插件生命周期 + 引导健壮性）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

2026-08-29 对照参照项目 `dsh-tauri-desk/deepseek-harness-desktop`（Rust/Tauri/React，1381★，v0.9.4）二次审核，确认我方在 batches 一~四（online-first）落地后仍有 5 项与参照的机制/健壮性/UX 不齐：

1. **companion（internal 类插件）仍未 spawn 前安装**——DesktopBootstrap 的随包插件装配走「dsh 起来后 后台 3s 装 → `host.Stop()` 重启 dsh → 导航」，而参照 `launch.rs` 明确**所有插件（含 internal）在 spawn dsh 前 ensure 就位、绝不安装后重启**（其 #177 时序原则）。效果 = 首启要两次启动 dsh（第一次无 companion，装完重启加载）。
2. **首启插件引导 UX**——参照有 `preinstall-setup` 引导页（推荐/修复/跳过 chip + 日志回流 + 取消事件）；我方首启只自动装 dshmarket，无用户选择/确认面与日志回流。
3. **下载健壮性**——参照 `download/` 有断点续传（Range）+ 多源镜像回落（官方→ghfast.top）+ 原子 staging/backup 切换 + Windows 文件锁重试；我方 `RuntimeBootstrap` 仅 `GetAsync→CopyTo` 单源 + SHA256 校验 + 重试，无上述保障。
4. **CLI shim / PATH 注册**——参照 `service/cli/shim/` 安装后把 `dsh`/`pnpm` 注入用户 PATH（Windows `%LOCALAPPDATA%\bin` shim + HKCU Path + `WM_SETTINGCHANGE`；mac/linux `~/.local/bin` + shell rc 幂等块）；我方无此能力。
5. **boot 假活看门狗**——参照 `desktop/plugin_boot.rs` 对「卡 Loading plugins」做有界恢复（有界刷新门控）；我方 `PageHealthMonitor` 只观测（阶段 1）不自动恢复。

有意设计差异（**不**在本 ADR 对齐）：下载模型（参照 zip 发行版 vs 我方 npm @latest）、端口（固定+自愈 vs `--port 0`）、自更新触发（参照 10min 轮询+toast vs 我方不轮询）、macOS 自更新（双方均手动 dmg）、迁移（参照幂等 migrate vs 我方有意不做）。前端栈（React SPA + iframe vs 我方 C#/Ryn）**延后专项讨论**，不在本 ADR 范围。

## Decision

按参照机制对齐 5 项（排序即实施批次），全部遵守既有 ADR 与质量门。

### 批次一 · companion 改 spawn 前安装（internal 时序对齐）✅

- 把「随包插件装配」从 dsh 起来后的后台 3s 任务，迁到 **spawn dsh 之前**：确定运行时（bundled/PATH-dsh 走 `host.StartAsync` 前；引导路径在 `BindRuntime` + `EnsureMarketFromRegistry` 后、`StartAsync` 前）→ `AssemblePending` → 有 pending 即 `plugin add` → 校验/补写 bundles → 再 `StartAsync`。
- 消除首启「先起 dsh 无 companion → 装 → 重启」的二次启动；首启 dsh 第一次即带 full 插件集。
- 保留 dev 隔离守卫（显式覆盖 home 时跳过）；`restartTriggered`/恢复覆写路径随之退役（不再需要安装后重启）。
- 失败语义：companion 安装 best-effort 仍只告警不阻断（缺 companion 不阻 dsh 起动），但改为 spawn 前尝试、失败留痕。
- ✅ 批次一已落地（2026-08-29，提交 `cfa4113`）：`MarketInstallHelper.EnsureBundledPluginsBeforeSpawnAsync` + 双路径 spawn 前接线，测试 389/389、覆盖率 52.3%。

### 批次二 · 首启插件引导页（preinstall UX 对齐）✅

- 静态 wwwroot 引导页增「插件引导」相：运行时就位后（BindRuntime 后）、`StartAsync` 前，若存在待装**可选**插件（当前仅 dshmarket 预设），引导页展示推荐 chip（默认勾选）+「确认安装/跳过」+ 安装日志回流；用户确认后宿主执行安装并把 `dsh plugin add` 的 stdout/stderr 推送回页面。跳过则该次不装（less-bootstrapped，但 dsh 起动后可从市场/设置自愈入口补装）。
- **与批次一 spawn 前安装合流**：companion（internal）保持 spawn 前静默自愈（不出现在勾选清单，对齐参照 `ensure_internal_plugins`）；dshmarket（preset）经引导页勾选后、`StartAsync` 前安装（对齐参照 `ensure_preset_plugins` + `preinstall-setup` UI）。
- **驱动模型**：引导页经 IPC `desktop.preinstall.choose`（`{"action":"install"|"skip"}`）发决策 → 宿主 `PreinstallChoiceGate`（值携带版 RuntimeBootstrapGate）放行引导循环 → 命中 install 才执行 `EnsureMarketFromRegistryAsync`。决策等待期宿主无 dsh 进程，监督器持续被 `bootstrapSettled` 门控防空转恢复。
- **安全兜底**：决策等待带 5 分钟超时，超时默认 SKIP（可恢复，dsh 照常起动；不默认装未确认的插件）——避免引导页未触发/用户离席导致壳永久挂在安装前，dsh 永不启动。
- **日志回流**：经专用 `dsh-desktop-preinstall` CustomEvent（`{kind:decision|installing|log|done}`）推送；安装日志流由 DesktopBootstrap 的流式进程执行器逐行转发（companion 安装仍走静默执行器，不触发回流）。
- **状态机呈现**：`BootstrapStep` 增 `PreinstallPlugins`（页面步骤序在 VerifyDsh 后呈现「插件准备」），引导页实际交互由 `dsh-desktop-preinstall` 事件驱动；RuntimeBootstrap 的步骤机仍严格为运行时下载/安装，不在其内混入插件引导。
- **实现**：`PreinstallChoiceGate`（值携带、可复位）+ `PreinstallCommandRouter` + `PresetPluginCatalog`（present preset 判定；当前仅 dshmarket）+ 流式进程执行器（`RunProcessStreamingAsync` 逐行转发 → `dsh-desktop-preinstall` 日志帧）承载回流；`MarketInstallHelper.EnsureMarketFromRegistryAsync` 签名不变（复用注入的 `runPluginAdd` 委托，靠执行器本身回流）。DesktopBootstrap 引导任务接线：BindRuntime → companion 自愈 → 待装 preset 判定 → 引导页决策 → 安装/跳过 → StartAsync。

### 批次三 · 下载健壮性（download 对齐）

`RuntimeBootstrap` 的 Node 发行包下载链路（`DownloadNodeAsync`）从「单源 `GetAsync→CopyTo` + 事后校验」升级为「摘要优先 + 多源回落 + 断点续传 + 原子落盘 + 锁重试」。四项子能力与取舍（2026-08-29 用户拍板）：

- **摘要优先取自官方（防投毒信任模型）**：先经 `FetchTextAsync` 拉**官方** `SHASUMS256.txt` 得可信摘要；后下载归档（官方→镜像）逐一用该可信摘要校验。官方摘要不可达即 fail loud（无可信摘要 → **不用镜像**，宁可中止不装坏包）。镜像只承担**下载**，绝不提供摘要（镜像提供摘要即自证自证、失去独立信任根）。
- **多源回落**：归档下载候选 = 官方 `NodeDistBaseUrl` → 镜像 `NodeMirrorBaseUrl`（默认 `https://cdn.npmmirror.com/binaries/node`，是 nodejs.org dist 的官方镜像）；镜像经 appsettings 可配置，**置空字符串 = 仅官方单源**（镜像关闭）。候选按序尝试，前一源失败（网络/异常）即切下一源；**同一 dest** 不删、续传。
- **断点续传（Range）**：下载落**确定性命名的 `.download-<runtimeDir 名>/<versionDir>/<fileName>`**（runtimeDir 同盘兄弟区，跨重试/跨进程存活，非每尝试随机 GUID，按 runtimeDir 名区分避免同父多运行时撞名）。生产 `DownloadFileAsync` 先读 dest 现有字节长，发 `Range: bytes=<n>-`；`206` 追加、`200`（服务端不支持 Range/文件已变）重头写、`416`（Range 不可满足=疑似已完整）重头写兜底——正确性由后续 SHA256 校验兜底。
- **原子 staging + 备份切换**：下载校验通过后，解压先落同盘兄弟临时目录 `.staging-<guid>/`（含 `extract`），归一为捆绑闭包同款扁平布局（node + npm 模块树）于 staging 根，**清掉 `extract/` 残余**（不把 include/share 等搬进正式目录）；成功再**原子 swap** 进正式 `runtimeDir`（`runtimeDir` → `.backup-<guid>`，staging → `runtimeDir`，成功后删 backup），失败清理 staging/恢复 backup。旧「逐件 `MoveInto` 进 runtimeDir」的半成品残留面消除。跨崩溃残留的 `.staging-*`/`.backup-*` 于下次下载前最佳努力清扫（防累积泄漏）。
- **Windows 文件锁重试**：swap 的 rename/remove 对 `IOException`（AV/文件扫描器瞬时锁）做**有界重试**（默认 10 次 × 200ms，达上限 fail loud）；`.download` 归档区清理为**最佳努力**（失败不阻塞引导结果）。
- 摘要仍来自 `SHASUMS256.txt`（既有）；`.download` 临时归档与正式 `runtimeDir` 分离，失败不残留半成品运行时。
- **测试策略**：既有 `RuntimeBootstrapTests` 的 fake hooks 改为**摘要先行**契约（FetchTextAsync 用常量摘要、DownloadFileAsync 写常量字节使其自洽），并新增回归：多源回落（主源抛→镜像命中）、续传（dest 已有字节→Range 路径）、原子 swap（staging 就位 runtimeDir、失败恢复 backup）、锁重试（前 N 次 IOException 后成功）、摘要缺失/不匹配 fail loud、镜像置空仅官方。真实 Range/206 行为由 `DSH_TEST_BOOTSTRAP_E2E` 门控 E2E 覆盖（不默认跑）。
- ✅ **批次三已落地**（2026-08-29，提交 `ba87738` feat + `e21cf78` docs(adr) + `3f16193` refactor(review) + `a54a459` docs(review) + `b0a6d72` docs(readme)）：三重审核 R1/R2/R3 串行——无 Blocker；收口 R2 B1（`extract/` 残余被 swap 进 runtimeDir）/B2（`DownloadWithFallbackAsync` 吞全异常）/R2 S1（跨崩溃残留 `.staging-*`/`.backup-*` 无清扫）；测试 **427/427**、覆盖率 **53.47%**、门禁全绿。**待续**：批次四（CLI shim）/五（假活看门狗）。

### 批次四 · CLI shim / PATH 注册（三个平台）

- 运行时就位后（每次启动对账）注册 `dsh`/`pnpm` shim：Windows `%LOCALAPPDATA%\deepseek-harness\bin` 生成 `.cmd`/`.ps1` + `HKCU\Environment\Path` 幂等追加 + `WM_SETTINGCHANGE` 广播；mac/linux `~/.local/bin` 落 shim + 既有 shell rc（`.bashrc`/`.zshrc`/`.profile`/`.bash_profile`/`.zprofile`/`.zlogin`）幂等更新块。bin 目录经 `DSH_DESKTOP_CLI_BIN_DIR` 可覆盖、rc home 经 `DSH_DESKTOP_CLI_RC_HOME` 可覆盖（dev/测试）。
- **dsh shim**：烘焙运行时（`<runtimeDir>` 含 node + `node_modules/@deepseek-ai/dsh/lib/bin.js`）与 `DSH_HOME`；节点解析序 = 本地兼容 node（≥24 或 22.15+ 或 23.8+）→ 运行时 node；执行 `node <runtimeDir>/*/bin.js "$@"`，`DSH_HOME`/`DSH_TELEMETRY_DISABLED` 仅在回退捆绑 dsh 时注入（转发用户自装 dsh 时保留用户环境）。用户自己已装 dsh（PATH 上排除本 shim 目录）优先转发，绝不覆盖用户配置。
- **pnpm shim**：优先用户自有 pnpm（PATH 排除本 shim 目录）；缺则输出「pnpm 未找到」提示。**online-first 适配偏差**——我方运行时（npm 装 dsh@latest）不捆绑独立 pnpm，参照项目随 zip 发行版内置 `dependencies/pnpm/bin/pnpm.cjs`；故本批次 pnpm shim 只承担「用户 pnpm 转发 + 诚实提示」，不假装自供给 pnpm（见 Alternatives）。
- 幂等合并、绝不覆盖用户配置；写入前说明路径（workflow 边界），失败仅告警不阻启动。桌面壳只在自己安装/首次登录对账时注册，避免与用户已有 PATH 冲突。
- dev 显式隔离（`DevEnvironment.IsDevRuntime`）时跳过 dsh shim 注册——避免把开发 home/runtime 烘焙进用户共享的终端 shim（对齐参照 debug 构建不写共享 dsh shim 的原则；Windows 同样遵守）。
- ✅ **批次四已落地**（2026-08-29；提交 `bec831d` feat + `c8eabb6` docs(adr) + `c428ea2` docs + `6992873` refactor(review)）：`CliShimBuilder`（纯 shim 文本生成，dsh/pnpm × cmd/ps1/sh）+ `CliShimPath`（PATH 幂等合并/rc 幂等块/生成标记识别）+ `CliShimPlanner`（按平台规划 shim 文件与 PATH 增量）+ `CliShimRegistrar`（定位运行时→烘焙→写 shim→注册 PATH；best-effort，失败仅告警；注册表写走 `[SupportedOSPlatform("windows")]` + 原始值读取保型 + `WM_SETTINGCHANGE` 广播；rc 只写已存在文件）。DesktopBootstrap 双路径（bundled/PATH-dsh 与引导完成后 `BindRuntime`）运行时就位后各注册一次；dev 隔离时跳过 dsh shim（Windows 亦遵守）。测试 **455/455**、覆盖率 **54.0%**、门禁全绿；三重审核 R1/R2/R3 串行收口（cmd DSH_HOME 注入时序、Windows dev 隔离失效、POSIX 谓词优先级、死 `DSH_NODE`/`RemovePathToken` 退役、注册表原始读保型、rc 补 `.zprofile` 等）。**待续**：批次五（boot 假活看门狗）。

### 批次五 · boot 假活看门狗（PageHealthMonitor 有界恢复）✅

- `PageHealthMonitor` 从「只观测」升级为「观测 + 有界恢复」：连续 Dead 达阈值后，先一次有界刷新（`NavigateAsync` 到记录的当前 dsh web URL）或触发一次有界重载；**有界**——恢复计数达上限即停止并 leave 观测面（防误报重启循环），恢复计数窗口随同成功复位。
- 对齐参照 `plugin_boot.rs`（Rust 编排层）的「卡 Loading plugins」恢复：探测到「dsh 在跑但页面空白」时，做有界 reload 而非无限轮询。
- **有意偏差（对齐属「能力等价」而非逐点等同）**：参照经 `plugin_boot.js.inc`（注入 iframe 的信号脚本）精确识别 `#root` 下「HARNESS + Loading plugins」boot 花屏才报 stalled，而我方直接加载 dsh web（非 iframe），探针以「body 无子节点即空白」为 Dead 信号——两者都捕获「dsh 进程在跑但页面没到应用态」的假活形态，信号粒度不同但恢复机致一致（有界 reload + leave 观测面）。
- ✅ **批次五已落地**（2026-08-29；提交 `2f12e69` feat + `cbbf70e` docs(adr) + `b44c18d` refactor(review)）：`PageHealthTracker.ReArm()`（重置死区，让 reload 后仍空白的页面重新凑满阈值再触发迁移）+ `PageHealthRecovery`（有界恢复预算：预算内允许 reload、耗尽转观测、成功恢复复位窗口，对齐参照 `BoundedReloadGate`）+ `PageHealthMonitor` 增 `HandleTransition`（Dead 迁移处经注入的 reload 委托触发，Alive 复位预算；reload 为 null 保持纯观测向后兼容）+ DesktopBootstrap 接线（reload 委托 = `webUrl` 非空时 `windowAccessor.Current.NavigateAsync(webUrl)`；崩溃恢复导航同步刷新 `webUrl`，防 reload 打到崩溃重启旧端口）。测试 **463/463**（+8：`PageHealthRecoveryTests` 7 例 + `PageHealthTracker.ReArm` 1 例）、覆盖率 53.7%、三重审核 R1/R2/R3 串行无 Blocker、门禁全绿。**参照对齐批次全部完成**。

## Alternatives considered

- **companion 维持在安装后重启**：功能可用，但违背参照「绝不安装后重启」原则，且首启两次启动 dsh 引入会话/端口二次初始化抖动。落败。
- **引入 React + Vite 换型前端**（完全等同参照）：技术栈等同让 UI/状态机逐点对齐成本最低，但引入前端构建链 + iframe 桥 + 容器化,与我方"C#/Ryn 稳定 + 静态 wwwroot + companion 插件"的既有稳定面冲突，属大换型。**延后专项讨论**（用户批示），本 ADR 先做能力等价（引导页增强）。
- **下载层多源镜像无摘要也镜像**：镜像只信任"有可信摘要"场景，否则第三方镜像投毒风险。落败。
- **摘要与归档同取自镜像**：镜像既给摘要又给归档即自证自证、失去独立信任根。落败；摘要只取自官方，镜像仅承担归档下载。
- **续传临时文件用每尝试随机 GUID**（现状 `.bootstrap-{guid}`）：每次重试换名、跨重试/跨进程无续传价值。落败；改确定性 `.download/<versionDir>/<fileName>`，跨重试/跨进程续传。
- **逐件 `MoveInto` 进 runtimeDir（现状）**：下载产物直接逐件搬入正式目录，中途失败留半成品（node 在而 npm 树缺等）。落败；原子 staging + 备份切换，失败恢复旧版。
- **CLI shim 写入系统 PATH（HKCU vs HKLM）**：只写用户级 HKCU + `~/.local/bin`，不触系统级；系统级需管理员且影响面大。落败。
- **pnpm shim 假装自供给 pnpm（online-first 下 bundle 一份 pnpm 随 shim 落盘）**：我方 online-first 去捆绑，运行时仅 node + npm 装的 dsh@latest，无独立 pnpm.cjs 可烘焙；为「自供给」而额外捆绑一份 pnpm 违背去捆绑转向且与 dsh 内部 `spawnSync("pnpm")` 的生态约定重叠。落败；本批次 pnpm shim 只做「用户 pnpm 转发 + 诚实提示」，dsh 插件安装所需 pnpm 仍依赖用户环境（与参照 `dependencies/pnpm` 的差异属有意，见 `有意设计差异`）。已记录为准。
- **pnpm shim 经运行时 node + npx 兜底拉取 pnpm**：能保证无全局 pnpm 时终端 `pnpm` 可用，但每次首调走 registry 下载、慢且需联网，作为日常命令 shim 体验差；且掩盖「pnpm 依赖用户环境」这一事实。落败；转发用户 pnpm，缺则明确提示。
- **dev 隔离下仍写共享 dsh shim**：开发运行时会把自己的 home/runtime 烘焙进用户终端共享的 `~/.local/bin/dsh`，污染生产命令行集成。落败；`DevEnvironment.IsDevRuntime` 显式跳过 dsh shim（pnpm shim 内容不烘焙 home/hash，dev 可写，与参照 debug 不写 dsh shim 一致）。
- **假活看门狗无限重载**：误报会形成重启/重载循环，伤害可用性。落败；有界恢复 + 计数上限。
- **逐点等同参照的精确 boot 花屏信号**（在 iframe 内精确识别 `#root` 下「HARNESS + Loading plugins」）：参照是 iframe 模型，精确识别其特定花屏形态；我方直载 dsh web（非 iframe），若也复刻该 DOM 锚点会随上游前端演进漂移制造假阴性，且捕获不到「同样假活但非该花屏」的形态。落败；以引擎层事实「body 无子节点即空白」为 Dead 信号，能力等价而非信号逐点等同。
- **插件引导塞进 RuntimeBootstrap 步骤机**：RuntimeBootstrap 是「运行时下载/安装」的严格状态机（Node+dsh），插件引导发生在 BindRuntime 之后、StartAsync 之前，属外层编排相——塞进去会混两个域并在其测试基线上造成混淆。落败；改为 `BootstrapStep` 增 `PreinstallPlugins` 只作页面步骤呈现，实际交互由专用 `dsh-desktop-preinstall` 事件 + `PreinstallChoiceGate` 驱动。
- **引导页无超时、永久等用户决策**：页面未触发/用户离席时壳会永久挂在安装前、dsh 永不启动（最坏天气）。落败；5 分钟超时默认 SKIP——默认「不装」比「装未确认插件」可恢复，dsh 照常起动、市场可从应用内补装。
- **跳过即永久不引导**：无重试入口会让跳过用户永远失去市场提示。落败；dsh 起动后市场/设置侧仍是自愈补装入口，且该次「跳过」只影响当前引导会话（下次引导时若仍未装会再次呈现）。
- **companion 也进勾选清单**：companion 是桌面壳必需品（internal），让用户跳过会得到残缺壳；对齐参照 `ensure_internal_plugins` 自愈语义。落败；companion 保持 spawn 前静默就位，仅 preset（dshmarket）可勾选。

## Consequences

- 首启：dsh 第一次启动即带 full 插件集（companion + dshmarket），无「装后重启」；引导页提供插件确认/跳过与日志回流。
- 下载：断网/弱网更稳（断点续传 + 镜像回落 + 原子落盘），失败不残留半成品，重试可续传。
- CLI：安装后用户可在终端直接用 `dsh`/`pnpm` 命令（环境生态一等公民的又一落点）。
- 假活：白屏自愈有界化，避免误报重启循环。
- 测试：每项配套回归（时序/状态机/下载重试/镜像 / shim 路径 / 有界恢复）；行为级变更走三重审核。
- 每批独立提交 + 门禁 + CI。

## Related

- [online-first-unbundled-runtime](2026-08-29-online-first-unbundled-runtime.md)（implemented）：本 ADR 在其首启引导/下载层之上对齐健壮性与插件时序；下载层改动直接作用于其 `RuntimeBootstrap`。
- [simple-shell-single-global-dsh](2026-08-31-simple-shell-single-global-dsh.md)（implemented）：**部分取代本 ADR 的"CLI shim / PATH 注册"批次**——dsh 已全局在 PATH（桌面不再生成 dsh shim，仅 pnpm shim；node 走系统全局并暴露到 PATH）。插件时序（spawn 前安装）保留（见该篇本 ADR 回链）。
- [plugin-surface-consolidation](../feature/2026-08-29-plugin-surface-consolidation.md)（implemented）：本 ADR 批次一/二在其「dshmarket 迁 spawn 前」基础上把 companion（internal）也收敛到 spawn 前，并补插件引导 UX。
- [page-health-monitor](../process/2026-08-26-page-health-monitor.md)（implemented）：批次五直接扩展其 `PageHealthMonitor`——从「阶段 1 只观测」升级为「观测 + 有界恢复」，其被取代的「阶段 2 未立项」陈述随批次五改写。
- [shell-observability-diagnostics](2026-08-24-shell-observability-diagnostics.md)（implemented）：PageHealthMonitor 观测面保留、有界恢复叠加其上。
- [desktop-shell-companion-plugin](../process/2026-08-21-desktop-shell-companion-plugin.md)（implemented）：companion 供给链与装配，本 ADR 批次一调整其装配时点。
- 参照项目 `dsh-tauri-desk/deepseek-harness-desktop`（v0.9.4，调研详录 `.plan/journal/2026-08-29-reference-project-key-differences.md`）。
- [split-program-main-god-function](2026-08-30-split-program-main-god-function.md)（implemented）：本 ADR 各批次的 `Program.cs` 接线随 P0 拆 Main 迁至 `DesktopBootstrap`。
