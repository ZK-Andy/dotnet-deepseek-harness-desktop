# Agent Note: online-first 去捆绑运行时——安装器瘦身 + 首启引导安装

Status: proposed

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

现行架构以「离线优先」为设计约束，为无网场景维护了一整套捆绑机器：

- 安装器捆绑完整运行时闭包（Node + dsh + 全量 node_modules，~300MB）；
- CI 为组装闭包维持 `bundle-runtime-ci.sh` 的组装/瘦身/sourcemap 剥离/六维签名缓存/pnpm store 缓存，`scriptSha256` 维度还要求脚本一改就全量重建；
- dshmarket 以「随包种子」进闭包（08-29 归化 ADR），连带钉版（MARKET_VERSION）、freshness 巡检、tgz 三重校验；
- dsh 内核升级与壳发版强耦合：上游每次 publish 都要走 bump → 发版链路，已装用户必须等我们发版才能吃到新内核。

用户反思定性：**离线本身是过度设计**——dsh 的价值在于连模型，用模型的场景必有网络。同赛道产品 dsh-tauri-desk/deepseek-harness-desktop（Tauri 版，两周 1361 星）以「5MB 安装器 + 首启联网下载」验证了该路线的可行性与传播力。本变更将 2026-08-23 共享 home ADR（「生态一等公民」）未走完的另一半走完：数据与运行时都归还生态，壳只管生命周期。

## Proposal

**策略转向：联网优先（online-first），离线从设计约束中删除。**

### 新首启形态

1. 安装器只带壳 + 自有插件（companion tgz），体积降至 10MB 级；
2. 首启引导：壳启动时检测运行时（本机 Node ≥ RuntimeVersionGate 底线则复用，否则下载钉版 Node zip 到 app-data，SHA256 校验）→ `npm install @deepseek-ai/dsh@latest`（npm 随 Node 分发，零额外依赖）→ registry 安装 dshmarket；
3. 首启 UI：Ryn 加载内置静态进度页（检测/下载/安装/失败重试状态机，fail loud），完成后导航到 dsh URL 进入既有主链路；
4. 存量升级（v0.3.12 → 新版）：安装器替换安装目录，`resources/runtime` 随之消失，首启检测缺失触发一次性下载。**不做迁移代码**。

### 版本策略

- dsh：`@latest`（跟 npm `latest` dist-tag）。上游现习惯 rc 直发 latest（实证：latest = 0.1.1-rc.2），预发行版可触达；若上游改用 `--tag next` 则自动隔离（同样是保护）。内核升级与壳发版解耦，`ERR_PNPM_NO_MATCHING_VERSION` 类等待链消失。`RuntimeVersionGate` 底线保留兜底；
- Node：钉版 zip（现役 24.20.0），来源 nodejs.org dist，下载带 SHA256 校验与重试。

### 插件随包范围收口（配合转向）

- **自有插件随包**：仅 companion（file: 自带安装，不涉网）；
- **dshmarket 降级**：种子机制对新增插件失去必要（唯一理由是离线首装），改为首启经 registry 安装；存量用户归化迁移逻辑保留（v0.3.12 存量已归化，自愈路径继续有效）；
- **第三方插件一律引导安装**（经 dshmarket），永不进闭包。

### 删除清单（实施批次二）

- `bundle-runtime-ci.sh` 闭包组装/trim/`trimPolicy`/`scriptSha256` 维度与闭包缓存、pnpm store CI 缓存；
- `BundledPluginCatalog.MarketRegistryFallback` 与 MARKET_VERSION 钉版 + `check-pin-freshness.sh` 巡检；
- `MarketInstallHelper` 随包种子安装路径（保留归化迁移）；
- 相关测试夹具（freshness clean/drift/inconsistent、闭包 staging 校验）与 cookbook 对应判别条目随代码退役。

### 实施批次

1. **批次一 · 首启引导**：RuntimeBootstrap（检测/下载/安装状态机）+ Ryn 静态进度页 + 失败重试；此批闭包仍在，行为为「bundled 优先、缺失走引导」（✅ 2026-08-29 已落地）；
2. **批次二 · 安装器与 CI 瘦身**：package-*.sh 停止捆绑闭包，删除清单逐项退役，preflight 同步；**必须先重构 `DevEnvironment.IsDevRuntime`**（吸收 [shared-home ADR 的在案挂账](../../implemented/architecture/2026-08-23-shared-home-desktop-profile.md)：其 dev 判定以「有无捆绑闭包」为信号之一，闭包消失 ⇒ 全部新装用户被判 dev → ApplicationId 带 .dev 后缀 + 随包插件安装被跳过。改为显式环境标记，不依赖闭包存在性探测）；
3. **批次三 · 插件面收口**：BundledPluginCatalog 收缩为 companion，dshmarket 转 registry 首装；companion tgz 改由安装器资源自带（file:，与闭包无关）；
4. **批次四 · 文档与实机**：architecture/user-guide/README 双语同步，实机验收转交（首启下载全链、存量升级触发一次性下载、断网 fail loud 文案）。

### 对齐竞品踩坑的设计约束（dsh-tauri-desk 已付学费，2026-08-29 调研）

- **config reconcile 先于启动**（其 #177 事故）：壳升级后 dsh 配置仍引用已消失的插件包会导致启动卡死循环。批次三退役 dshmarket 种子时，DesktopProfileBootstrap 必须先 reconcile 配置中不再存在的引用再启动，不允许 unresolvable bundle 引用残留；
- **每步装完验证产物入口**（其插件 readiness 竞态修复链）：引导状态机不做 fire-and-forget，下载/安装/校验每步完成即验证产物（入口文件/版本号）存在再标成功；
- **env 路径规范化**（其 #198）：传给子进程的路径剥 Windows 扩展长度前缀（`\\?\`），避免破坏下游 shim/脚本解析；
- **子进程流显式 UTF-8**（其 #197 非 UTF-8 管道崩溃的 .NET 变体）：.NET replacement fallback 结构上免疫崩溃类，但 HarnessRuntimeHost spawn 未设 StandardOutput/ErrorEncoding，Windows 中文区域按 OEM 码页解码 UTF-8 输出会乱码——批次一同址补 `Encoding.UTF8` 显式声明；
- **引导失败不损已装内核**（其 P1 验收点）：RuntimeLocator bundled→PATH→引导下载的回退序保持——下载/安装任一步失败，已有可用内核照常启动，只 fail loud 提示本次未完成的部分。

### 批次一实证教训（2026-08-29，E2E 与沙箱全链实跑）

- **npm 对无 package.json 目录的 `npm install <pkg>` 是静默 no-op**：exit 0 但什么都不装（E2E 实证）。引导在安装前必须先落最小 package.json（`dsh-desktop-runtime`，private），并以此为安装前置产物校验点；
- **npm 装 dsh 依赖树（454 包）堆峰值 ~1.7-3GB**：默认 V8 堆上限（≈物理内存一半）在 8G 内存机器会 abort（exit 134），显式 `NODE_OPTIONS=--max-old-space-size=3072` 强制积极 GC 可稳过（仅注入 npm-cli 调用，不污染 dsh 运行时堆行为）；
- **Node 发行包布局 ≠ 捆绑闭包布局**：发行包为 `bin/node` + `lib/node_modules/npm`，闭包探测形态为根级 `node(.exe)` + 平台相关 npm 模块树——解压后必须归一（搬 `bin/node`→根、npm 树→平台相对路径、删 include/share 等杂物），否则 RuntimeLocator 永远探测不到。

## Alternatives considered

- **保留离线优先不动**：维护成本持续累积（CI 闭包机器、钉版巡检、bump 发版耦合），且服务的场景（无网用模型）被证明不存在。落败。
- **dsh 仍钉版下载**：确定性最强，但内核升级重新耦合壳发版，转向的核心红利（解耦）消失。落败；以 RuntimeVersionGate 底线 + 壳发版抬底线兜住稳定性。
- **Node 一律下载不复用本机**：避免版本漂移，但对已有 Node 的开发者多一次 30MB+ 下载，与竞品实践不符。落败；本机 ≥ 底线则复用。
- **dshmarket 仍随包**：离线首装出市场是唯一论据，随离线约束删除而失效；随包种子机制（归化、钉版、巡检）整体退役更简洁。落败；存量迁移逻辑保留。

## Acceptance criteria

- 新装：安装器不含 `resources/runtime`，首启在有网环境完成 Node/dsh/dshmarket 安装并进入主界面，全过程进度页可见、失败 fail loud 可重试；
- 存量升级：升级后首启触发一次性运行时下载，无迁移代码，dsh 数据（共享 home）不受影响；
- CI：打包流水线不再组装/缓存闭包，打包时长显著下降；freshness 巡检退役；
- 壳发版与 dsh 内核升级解耦：上游 publish 后已装用户不经壳发版即可获取新内核。

## Risks

- **npm 可达性**（弱网/镜像场景）：首装可能慢或失败——进度页重试 + 文档给镜像配置指引；竞品同路线已被市场验证；
- **上游 breaking 变化直触达用户**：`@latest` 使上游缺陷无缓冲直达，靠 RuntimeVersionGate 底线（出问题立即抬）与自有回归观察兜底；
- **首启体验新增失败面**：下载/安装/校验每步都可能失败，进度页状态机需完整错误呈现（对齐竞品 docs/testing/01-install/05 的失败场景清单）；步骤超时（StepTimeoutMinutes）已接线兜底网络停滞；
- **SHA256 校验是防损坏而非信任锚**：摘要与文件同源同一 base url，`NodeDistBaseUrl`/`DshSpec` 可配置时校验无信任增量（能改配置的攻击者等权能改运行时目录）——本变更接受此边界，与自更新栈「release 侧复取哈希锚点」的强校验不同级；
- **Node 钉版副本新增巡检盲区**：`RuntimeBootstrapOptions.NodeVersion`（appsettings）是与 bundle-runtime-ci.sh NODE_VERSION 并存的手工同步副本，check-pin-freshness.sh 未覆盖——批次二退役捆绑钉版前该漂移面存在（若批次二提前，先扩巡检副本集）；
- **放弃的东西**：离线可用性（有意放弃）；闭包缓存带来的打包提速（由不再组装闭包直接取代）；随包种子的「逐字节等价」确定性（registry 安装天然等价）。

## Related

- [shared-home-desktop-profile](../../implemented/architecture/2026-08-23-shared-home-desktop-profile.md)（implemented）：本 ADR 取代其「运行时归属 B 形态：闭包保留为预览期形态、去捆绑是远期选项」条款与 dev 判定挂账——它自注「届时另立 ADR」，本篇即该 ADR；共享 home / desktop profile / 数据互通等其余决定全部保留。
- [bundled-plugin-registry-normalization](../../implemented/feature/2026-08-29-bundled-plugin-registry-normalization.md)（implemented）：本 ADR 取代其「随包 = 种子保离线可靠」前提与「MARKET_VERSION 钉版语义（freshness 巡检职责不变）」一条（批次二/三退役）；归化迁移机制与存量自愈路径被显式保留。
- [ryn-shell-bundled-dsh-runtime](../../implemented/architecture/2026-08-20-ryn-shell-bundled-dsh-runtime.md)（implemented）：本 ADR 直接转向其「完整运行时打包」决定，捆绑闭包与「零下载确定性」差异化表述随 offline 约束删除而退役。
- [companion-plugin-version-aware-upgrade](../../implemented/feature/2026-08-22-companion-plugin-version-aware-upgrade.md)（implemented）：随包种子退役后 tgz 供给渠道变化见批次三；版本比对机制不变。
