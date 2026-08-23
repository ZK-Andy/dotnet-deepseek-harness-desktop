# Agent Note: shared-home-desktop-profile

Status: proposed

## Problem

产品态使用私有 `DSH_HOME`（`~/.local/share/DeepSeek.Harness.Desktop/dsh`），与用户 CLI 世界（`~/.dsh`）形成两个数据宇宙：会话、凭据、工作区、插件互不可见。这与上游数据模型相悖——源码实证：① `boot/cmdline` 文档自举 `dsh --profile tui`，profile 即命名启动配置（web/tui 同构）；② `sessions/` 位于 home 层与 `profiles/` 平级（本项目两个 home 磁盘布局均如此），会话天然跨 profile 共享；③ `util/home-paths` 定义规范 home `~/.dsh` + `DSH_HOME` 覆盖供全生态共用。

本笔记取代同日被整体删除的初版提案：方向主干经评审**确认成立**，其携带的迁移管控设计被**否决**，破坏性定位**拍板确认**——此处只保留存活结论与否决理由。

## Proposal

- 产品桌面端默认改用上游规范 home（`~/.dsh`，经 `DSH_HOME` 可覆盖）；**运行时闭包仍随包内置**——零环境承诺不变，共享的是数据不是二进制。
- 桌面使用专属 profile `~/.dsh/profiles/desktop`（自有 cordis.yml/bundles，companion 装入此处），不读写 `profiles/web`：插件组合互不竞态，「插件管理 = 管理 profile 配置」。
- home 级数据（sessions/workspaces/credentials/settings）与其他前端（CLI/TUI/Web）天然互通。
- 开发期守卫不回退：dev 构建继续自动隔离 home（防污染真实数据），与本决定分属两码事。
- **破坏性变更是既定代价（已拍板）**：存量私有 home 不做自动迁移、不做兼容层；告知责任由 README「预览版破坏性变更风险提示」承担（升级前自行备份）。私有 home 数据原地保留不删，用户可随时以 `DSH_HOME` 指回旧路径。

## Alternatives considered

- **维持私有 home**：落败——数据宇宙割裂违背上游 home/profile 模型，对 CLI 重度用户是硬伤；且与「插件管理即配置」的生态方向冲突。
- **Attach 已运行的 dsh web 实例**（ccgui 模式）：暂缓——版本漂移治理复杂、进程生命周期权威混乱，且破坏零环境默认；SDK `stdio` JSON-RPC（`packages/sdk/server`）作为更正规的通道后续单独评估。
- **file:// dist + IPC bridge 深嵌入**（`host/webserver` 注释点名的 Electron 形态）：远期记录——去掉 loopback HTTP 需自建 fetch/WS 桥，大改造非当下。
- **存量迁移管控机制**（状态判定矩阵 + Journal 幂等执行协议 + copy→verify→rename-backup + 分阶段灰度 Stage 0–5）：**评审否决**——预览阶段承担不起这套复杂度与长期维护面；灰度期双模式并存本身制造新的状态空间；项目定位已明确「不保证跨版本数据兼容」（README 预览提示），迁移机器的收益覆盖不了成本。切换直接到位，备份提示兜底。

## Risks

- 共享 home 无锁层：并发写同 profile 的 pnpm 安装存在竞态——专属 `desktop` profile 规避主要冲突面；home 级并发语义与 CLI 用户现状一致，依赖上游容忍度。
- schema 偏斜：bundled rc.x 与用户 CLI 版本差作用于共享 settings/sessions——切换实现前需盘点可靠的版本信号（原 Stage 0 研究点收窄为一次前置检查，不做通用框架）；插件会在 home 根自建未知数据目录（如 `dsh-pocket/`），桌面侧逻辑必须容忍而非清理它们。
- 存量用户升级即断：桌面不再读私有 home，属预期内的破坏性变更；README 提示 + `DSH_HOME` 回退是仅有的两条出路。

## Related

- [dev 运行时隔离](../../implemented/process/2026-08-22-dev-runtime-isolation.md)：dev 隔离守卫保留，本决定仅改变产品态 home 归属。
- [companion 版本感知升级](../../implemented/feature/2026-08-22-companion-plugin-version-aware-upgrade.md)：比对逻辑不变，作用对象改为 `profiles/desktop`。
