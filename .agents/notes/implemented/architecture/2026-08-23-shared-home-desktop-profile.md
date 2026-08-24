# Agent Note: shared-home-desktop-profile

Status: implemented

## Problem

产品态使用私有 `DSH_HOME`（`~/.local/share/DeepSeek.Harness.Desktop/dsh`），与用户 CLI 世界（`~/.dsh`）形成两个数据宇宙：会话、凭据、工作区、插件互不可见。这与上游数据模型相悖——源码实证：① `boot/cmdline` 文档自举 `dsh --profile tui`，profile 即命名启动配置（web/tui 同构）；② sessions/credentials 位于 home 层与 `profiles/` 平级（credentials-local 源码钉死 `<home>/.credentials.yaml`），会话天然跨 profile 共享；③ `util/home-paths` 定义规范 home `~/.dsh` + `DSH_HOME` 覆盖供全生态共用。

## Decision

**最小工作理念（一句，不立主义）**：给图形界面原住民一个顺手把活干得更好的桌面端。

**共享 home（数据一份）——已落地**：

- `HarnessRuntimeHost.ResolveDshHome()` 优先级：`DSH_DESKTOP_DSH_HOME`（dev 隔离自动写入/用户显式回退）> 生态标准 `DSH_HOME`（与上游同语义：空白视为未设、支持 `~` 前缀）> 规范默认 `~/.dsh`。
- 启动组装走专属 `profiles/desktop`：spawn 参数与 Program.cs 插件任务的 profile 引用共用单点常量 `HarnessRuntimeHost.DesktopProfileName`。
- **desktop profile 由壳自举**（实现期发现的关键前置）：上游 app-boot 只对内置模板（web/headless）自动初始化 profile，自定义名在 `package.json` 缺失时直接拒启。`DesktopProfileBootstrap.EnsureProfile` 在首次 spawn 前按上游 `initProfile` 同款三件套自举（清单 + 空 patch 层 + pnpm-workspace），bundles 对齐 web 模板（`dsh-base` + `dsh-web-app`——缺 web-app 则永远出不了 `dsh web:` URL）；幂等且永不覆写已存在文件。随包插件安装完成后补回这两个必需 bundle，防上游 reconcile 重整时丢失桌面 Web UI 层。
- **破坏性变更是既定代价（已拍板）**：存量私有 home 不做自动迁移、不做兼容层；一次性只读提示落地为 `LegacyHomeNotice`（检测旧私有 home 存在 → host.log + 界面横幅告知新位置与回退方式，无持久标记）；`DSH_DESKTOP_DSH_HOME` 指回即回退。
- **启动版本底线检查**（版本偏斜唯一防线）：`RuntimeVersionGate` 只读探测即将执行的 dsh 版本，低于底线（`0.1.1-rc.2`，与闭包钉版同源升级）仅日志+横幅明确提示，不阻断；探测失败按未知处理只记日志。数字段逐段比较，同核预发布后缀不参与（粗粒度足够拦截跨 minor 老运行时）。
- **运行时归属（B 形态核心）**：随包捆绑闭包保留为预览期形态——「零下载确定性」是在案差异化点；去捆绑降级为远期选项（触发条件：① 上游 dsh 发布 stable；② 供给链出现可借鉴成熟实践），届时另立 ADR。
- 开发期守卫不回退：dev 自动隔离逻辑不变（其判定依赖捆绑闭包存在性，本形态下继续有效）。

**实施范围（收窄为纯 home 半场）**：home 解析、spawn/profile 切换与自举、旧 home 提示、版本底线检查、配套测试（含 E2E 门控的布局契约断言）、README 双语与 docs 同步（user-guide 数据位置表随切换同 PR 改写）。供给链相关组件（在线拉取、安装器、PATH 注册）一律不建。

## Alternatives considered

- **全量切换（共享 home + 立即去捆绑）**：落败——供给链是唯一真正的新工程且落在测试矩阵最薄的平台带上；首启网络依赖放弃零下载确定性；同日考察的两个头部先例均未走此路（anywhere-labs：官方 profile 共用同一 DSH home **且**随包自带 Node+dsh 依赖；opencode：desktop 不覆写 `XDG_DATA_HOME` 与 CLI 共享数据 home，server 副本随 app 分发）。「生态一等公民」的落点是数据互通与生态参与，不以去捆绑为必要条件。
- **维持私有 home 全自包含**：落败——数据宇宙割裂违背上游 home/profile 模型，对 CLI 重度用户是硬伤；与「插件管理即配置」的生态方向冲突。
- **存量迁移管控机制**（状态判定矩阵/Journal 幂等/copy→verify→rename-backup/灰度 Stage 0–5）：评审否决——预览阶段承担不起这套复杂度与长期维护面；灰度期双模式并存制造新状态空间。切换直接到位，备份提示兜底。
- **让 `plugin add` 首装时自然创建 desktop profile**：落败——`plugin add` 以 `DEFAULT_PROFILE_BUNDLES`（仅 `dsh-base`）初始化自定义名 profile，缺 `dsh-web-app` 则壳拿不到 Web UI URL；且创建时机晚于首次 spawn（拒启在先）。壳侧 spawn 前自举是唯一能保证首启成功的位置。
- **companion 插件公开发布**（npm/dshmarket 独立分发）：落败——其全部价值寄生宿主（slot 与 `desktop.update.*` 命名空间离开桌面壳即死代码）。通道定为随壳本地 tgz；工程归属同仓独立子项目，壳打包显式钉版消费。
- **Attach 已运行的 dsh web 实例**（ccgui 模式）：暂缓——版本漂移治理复杂、进程生命周期权威混乱，且破坏零环境默认。

## Consequences

- 桌面与 CLI/TUI/Web 共享同一数据宇宙：一侧装的会话/凭据/插件另一侧可见；桌面插件装配隔离在 `profiles/desktop`，互不干扰。
- 版本偏斜形态改变：桌面自带钉版 rc 与用户自行升级的 CLI 写同一 home；底线检查是唯一防线（只读提示，不做矩阵/灰度）。opencode 以「数据共享 + 执行副本随包」接受同类偏斜，先例在案。
- 自举文件与上游 `initProfile` 格式存在漂移面：格式逐字对齐 rc.2 并在代码注释钉源；上游若改格式需同步（fail loud 兜底——格式不被接受时 dsh 拒启走既有降级页）。
- 项目级内容跨版本兼容无上游承诺：高价值层兜底不变（不动旧 home 原数据；对插件自建的未知目录只容忍不清理）；剩余风险经 README/user-guide 明示。
- dev 判定的耦合（挂账防漏改）：`DevEnvironment.IsDevRuntime` 以「有无捆绑闭包」为信号之一，本形态下闭包保留故信号继续有效；远期若真去捆绑，此项必须先行重构。

## Related

- [dev 运行时隔离](../process/2026-08-22-dev-runtime-isolation.md)：dev 隔离守卫保留，本决定仅改变产品态 home；其判定所依赖的捆绑闭包信号在本形态下不受影响。
- [companion 版本感知升级](../feature/2026-08-22-companion-plugin-version-aware-upgrade.md)：比对逻辑不变；作用对象改为 `profiles/desktop`，tgz 供给渠道不变（随壳本地 tgz）。
