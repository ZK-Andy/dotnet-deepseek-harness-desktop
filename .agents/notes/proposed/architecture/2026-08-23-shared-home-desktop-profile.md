# Agent Note: shared-home-desktop-profile

Status: proposed

## Problem

产品态使用私有 `DSH_HOME`（`~/.local/share/DeepSeek.Harness.Desktop/dsh`），与用户 CLI 世界（`~/.dsh`）形成两个数据宇宙：会话、凭据、工作区、插件互不可见。这与上游数据模型相悖——源码实证：① `boot/cmdline` 文档自举 `dsh --profile tui`，profile 即命名启动配置（web/tui 同构）；② `sessions/` 位于 home 层与 `profiles/` 平级（本项目两个 home 磁盘布局均如此），会话天然跨 profile 共享；③ `util/home-paths` 定义规范 home `~/.dsh` + `DSH_HOME` 覆盖供全生态共用。

同一孤岛逻辑也存在于运行时侧：随包捆绑 node+dsh 闭包（百 MB 级，包体绝对大头）意味着桌面自养一个钉死版本的 dsh 副本，与用户 CLI 安装的 dsh 构成第二组平行宇宙——数据、二进制双双与生态割裂。

本笔记取代同日被整体删除的初版提案：初版的方向主干经评审确认成立、其携带的迁移管控设计被否决；随后产品哲学进一步收敛，运行时归属一并拍板，此处为现行完整结论。

## Proposal

**产品哲学（决定之母）：桌面端是 dsh 生态的一等公民，不做自包含孤岛——凡生态已有的东西，共享生态的那一份。**

- **共享 home（数据一份）**：产品态默认改用上游规范 home（`~/.dsh`，经 `DSH_HOME` 可覆盖）；桌面使用专属 profile `~/.dsh/profiles/desktop`（自有 cordis.yml/bundles，companion 装入此处），不读写 `profiles/web`，插件组合互不竞态；home 级数据（sessions/workspaces/credentials/settings）与其他前端天然互通。
- **共享运行时（二进制一份）**：不再随包捆绑 node+dsh 闭包；桌面使用生态的 dsh 运行时，缺失时替用户供给一份（供给方式——随包携带安装器 / 首启在线拉取 / 复用既有全局安装——属实现期择优，非本决定范围）。目标形态为全机单一 dsh，CLI 与桌面天然同版本，「零环境」重新定义为「零手动配环境」，而非「零安装」。
- **破坏性变更是既定代价（已拍板）**：存量私有 home 不做自动迁移、不做兼容层；告知责任由 README「预览版破坏性变更风险提示」承担（升级前自行备份）；私有 home 数据原地保留不删，`DSH_HOME` 指回即回退。
- **版本偏斜接受为预览期常态**：上游 rc.x 自带破坏性演进且新旧 home 内容不兼容已实际出现；桌面跟随生态版本而非以内部副本隔离之，上游升级波及桌面属生态共担。唯一防线 = 启动版本底线检查（只读探测 dsh 版本，低于桌面要求则明确提示，不做矩阵/日志/灰度那套迁移管控）。
- **数据保护分层**：项目级落盘内容（storages/workspaces 等用户工作成果）为高价值层，切换及后续演进中不得静默损毁，实现前盘点具体清单；会话内容按可丢弃对待（现状本就不保证跨版本可用，不为它加机制）。
- **切换时机：尽快**——趁用户量小一次性断到位，不设过渡双轨。
- 开发期守卫不回退：dev 构建继续自动隔离 home（防污染真实数据）。

## Alternatives considered

- **维持私有 home**：落败——数据宇宙割裂违背上游 home/profile 模型，对 CLI 重度用户是硬伤；且与「插件管理即配置」的生态方向冲突。
- **维持随包捆绑闭包（含「默认捆绑 + 探测到兼容 PATH dsh 则优先复用」的混合变体）**：落败——与私有 home 同病：给桌面再造一个平行运行时宇宙，自包含孤岛与生态一等公民定位直接矛盾；百 MB 级闭包 × 三平台 × 每次发版的分发与自更新下载成本巨大；混合变体额外引入双代码路径与「兼容窗」的定义维护面，用复杂度买不来哲学一致性。包体瘦身的针对性优化随闭包取消而消解，不再单独立项。
- **存量迁移管控机制**（状态判定矩阵 + Journal 幂等执行协议 + copy→verify→rename-backup + 分阶段灰度 Stage 0–5）：评审否决——预览阶段承担不起这套复杂度与长期维护面；灰度期双模式并存本身制造新的状态空间；项目定位已明确「不保证跨版本数据兼容」，迁移机器的收益覆盖不了成本。切换直接到位，备份提示兜底。
- **companion 插件公开发布**（npm / dshmarket 独立分发）：落败——其全部价值寄生宿主（slot 与 `desktop.update.*` 命名空间离开桌面壳即死代码），无独立用户即无独立发布与支持意义。通道定为**随壳本地 tgz**；工程归属为**同仓独立子项目**（自有构建管线产出带版本 tgz，壳打包显式钉版消费，禁止「拉最新」——防陈旧工件事故重演）；迁出独立仓库的触发条件为出现真实独立发布价值。
- **Attach 已运行的 dsh web 实例**（ccgui 模式）：暂缓——版本漂移治理复杂、进程生命周期权威混乱，且破坏零环境默认；SDK `stdio` JSON-RPC（`packages/sdk/server`）作为更正规的通道后续单独评估。
- **file:// dist + IPC bridge 深嵌入**（`host/webserver` 注释点名的 Electron 形态）：远期记录——去掉 loopback HTTP 需自建 fetch/WS 桥，大改造非当下。

## Risks

- 上游 rc.x 的破坏性演进直接作用于桌面：用户升级全局 dsh 可能立即使桌面异常——接受为生态共担，缓解仅限版本底线提示；桌面自身发版节奏须紧跟上游，缩小暴露窗。
- 首启供给链的失败面（网络代理、pnpm/npm 政策拦截如 minimumReleaseAge、写入权限）：实现期逐项盘点，任何一步失败必须 fail loud 给出指引，禁止静默半装状态。
- 项目级内容跨版本兼容无上游承诺：高价值层当前只有两条被动兜底（切换不动旧 home 原数据；对插件在 home 根自建的未知目录如 `dsh-pocket/` 只容忍不清理），剩余风险经 README 明示。
- 共享 home 无锁层：并发写同 profile 的 pnpm 安装存在竞态——专属 `desktop` profile 规避主要冲突面；home 级并发语义与 CLI 用户现状一致，依赖上游容忍度。

## Related

- [dev 运行时隔离](../../implemented/process/2026-08-22-dev-runtime-isolation.md)：dev 隔离守卫保留，本决定仅改变产品态 home 与运行时归属。
- [companion 版本感知升级](../../implemented/feature/2026-08-22-companion-plugin-version-aware-upgrade.md)：比对逻辑不变；作用对象改为 `profiles/desktop`，其 tgz 由同仓独立子项目供给（见 Alternatives）。
