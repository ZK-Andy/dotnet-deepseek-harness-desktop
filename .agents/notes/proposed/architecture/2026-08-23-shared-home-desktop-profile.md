# Agent Note: shared-home-desktop-profile

Status: proposed

## Problem

产品态使用私有 `DSH_HOME`（`~/.local/share/DeepSeek.Harness.Desktop/dsh`），与用户 CLI 世界（`~/.dsh`）形成两个数据宇宙：会话、凭据、工作区、插件互不可见。这与上游数据模型相悖——源码实证：① `boot/cmdline` 文档自举 `dsh --profile tui`，profile 即命名启动配置（web/tui 同构）；② `sessions/` 位于 home 层与 `profiles/` 平级，会话天然跨 profile 共享；③ `util/home-paths` 定义规范 home `~/.dsh` + `DSH_HOME` 覆盖供全生态共用。

运行时侧的平行宇宙问题（随包捆绑 node+dsh 闭包）经 2026-08-23 复议重新定性：**预览期保留，不再是本次要拆除的对象**（见 Proposal）。

本笔记同日两次修订：初版提案被整体删除后重立（方向主干成立、迁移管控否决）；随后全量去捆绑降级为远期选项，运行时半场改为保留随包闭包，此处为现行完整结论。

## Proposal

**最小工作理念（一句，不立主义）**：给图形界面原住民一个顺手把活干得更好的桌面端。界面说用户的母语（拖放、右键、原生窗口习惯），每一处可视化都为质量或效率还债。更完整的理念思辨存 `.plan/`（本地原料，未经验证，不立为教条），待产品行为积累后再提炼。

**共享 home（数据一份，现在切）**：

- 产品态默认改用上游规范 home（`~/.dsh`，经 `DSH_HOME` 可覆盖）；桌面使用专属 profile `~/.dsh/profiles/desktop`（自有 cordis.yml/bundles，companion 装入此处），不读写其他 profile；home 级数据（sessions/workspaces/credentials/settings）与其他前端天然互通。
- **破坏性变更是既定代价（已拍板）**：存量私有 home 不做自动迁移、不做兼容层；告知责任由 README「预览版破坏性变更风险提示」承担；私有 home 数据原地保留，`DSH_DESKTOP_DSH_HOME` 指回即回退。可做的一次性只读提示（检测旧私有 home 存在则告知新位置与旧数据路径）不属于迁移管控。
- **切换时机尽快**，趁用户量小一次性断到位，不设过渡双轨。
- 开发期守卫不回退：dev 自动隔离逻辑不变（其判定依赖捆绑闭包存在性，本形态下继续有效）。

**实施范围（收窄为纯 home 半场）**：`HarnessRuntimeHost.ResolveDshHome` 默认路径改规范 home、spawn 参数与 Program.cs 插件任务的 `--profile web` → `desktop` 及 profile 目录引用、一次性旧 home 只读提示、配套测试、README 双语与 docs 同步。供给链相关组件（在线拉取、安装器、PATH 注册）一律不建。

**运行时归属（本次修订核心）**：随包捆绑闭包（node+dsh+dshmarket+companion tgz）**保留为预览期形态**——「零下载确定性」是竞品调研在案的差异化点；而供给链（替用户装 node+dsh 的唯一路径）是唯一全新的工程，失败面集中在没有真机的 Windows/macOS 平台。**去捆绑降级为远期选项**，满足其一再立项：① 上游 dsh 发布 stable；② 供给链出现可直接借鉴的成熟实践（如 hairyf 首启装配模式获进一步社区验证）。届时另立 ADR，不在本文预写方案。

## Alternatives considered

- **全量切换（共享 home + 立即去捆绑，本笔记上一版的立场）**：落败——供给链是唯一真正的新工程且落在测试矩阵最薄的平台带上；首启网络依赖放弃零下载确定性；同日考察的两个头部先例均未走此路（anywhere-labs：官方 profile 共用同一 DSH home **且**随包自带 Node+dsh 依赖；opencode：desktop 不覆写 `XDG_DATA_HOME` 与 CLI 共享数据 home，server 副本随 app 分发）。「生态一等公民」的落点是数据互通与生态参与，不以去捆绑为必要条件。
- **维持私有 home 全自包含**：落败——数据宇宙割裂违背上游 home/profile 模型，对 CLI 重度用户是硬伤；与「插件管理即配置」的生态方向冲突。
- **存量迁移管控机制**（状态判定矩阵/Journal 幂等/copy→verify→rename-backup/灰度 Stage 0–5）：评审否决——预览阶段承担不起这套复杂度与长期维护面；灰度期双模式并存制造新状态空间。切换直接到位，备份提示兜底。
- **companion 插件公开发布**（npm/dshmarket 独立分发）：落败——其全部价值寄生宿主（slot 与 `desktop.update.*` 命名空间离开桌面壳即死代码）。通道定为随壳本地 tgz；工程归属同仓独立子项目，壳打包显式钉版消费；迁出独立仓库的唯一触发条件为出现真实独立发布价值。
- **Attach 已运行的 dsh web 实例**（ccgui 模式）：暂缓——版本漂移治理复杂、进程生命周期权威混乱，且破坏零环境默认。
- **file:// dist + IPC bridge 深嵌入**：远期记录——去掉 loopback HTTP 需自建 fetch/WS 桥，大改造非当下。

## Risks

- **版本偏斜形态改变**：共享 home 后，桌面自带钉版 rc 与用户自行升级的 CLI 写同一 home。缓解 = 启动版本底线检查（只读探测，低于底线明确提示，不做矩阵/灰度）；opencode 以「数据共享 + 执行副本随包」接受同类偏斜，先例在案。
- **项目级内容跨版本兼容无上游承诺**：高价值层兜底不变（不动旧 home 原数据；对插件自建的未知目录只容忍不清理）；剩余风险经 README 明示。
- **共享 home 无锁层**：并发写同 profile 的 pnpm 安装竞态——专属 desktop profile 规避主要冲突面；home 级并发语义与 CLI 用户现状一致，依赖上游容忍度。
- **dev 判定的耦合（挂账防漏改）**：`DevEnvironment.IsDevRuntime` 以「有无捆绑闭包」为信号之一，本形态下闭包保留故信号继续有效；远期若真去捆绑，此项必须先行重构。

## Related

- [dev 运行时隔离](../../implemented/process/2026-08-22-dev-runtime-isolation.md)：dev 隔离守卫保留，本决定仅改变产品态 home；其判定所依赖的捆绑闭包信号在本形态下不受影响。
- [companion 版本感知升级](../../implemented/feature/2026-08-22-companion-plugin-version-aware-upgrade.md)：比对逻辑不变；作用对象改为 `profiles/desktop`，tgz 供给渠道不变（随壳本地 tgz）。
