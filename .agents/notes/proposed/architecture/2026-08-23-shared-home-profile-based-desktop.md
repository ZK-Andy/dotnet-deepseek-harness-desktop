# Agent Note: shared-home-profile-based-desktop

Status: proposed

## Problem

本项目产品态使用私有 `DSH_HOME`（`~/.local/share/DeepSeek.Harness.Desktop/dsh`），与用户 CLI 世界（`~/.dsh`）形成两个数据宇宙：会话、凭据、工作区、插件互不可见。这与上游数据模型相悖——源码实证：①`boot/cmdline` 文档自举 `dsh --profile tui`，profile 即命名启动配置（web/tui 同构）；②`sessions/` 位于 home 层与 `profiles/` 平级（本项目两个 home 磁盘布局均如此），会话天然跨 profile 共享；③`util/home-paths` 定义规范 home `~/.dsh` + `DSH_HOME` 覆盖供全生态共用。

## Proposal

- 产品桌面端默认改用上游规范 home（`~/.dsh`，经 `DSH_HOME` 可覆盖）；**运行时闭包仍随包内置**——零环境承诺不变，共享的是数据不是二进制。
- 桌面使用专属 profile `~/.dsh/profiles/desktop`（自有 cordis.yml/bundles，companion 装入此处），不读写 `profiles/web`：插件组合互不竞态，「插件管理 = 管理 profile 配置」。
- home 级数据（sessions/workspaces/credentials/settings）与其他前端（CLI/TUI/Web）天然互通。
- 开发期守卫不回退：dev 构建继续自动隔离 home（防污染真实数据），与本产品决策分属两码事。
- 存量迁移：提供一次性导入（sessions/workspaces/credentials 从私有 home 拷入 `~/.dsh`）。
- 前置研究：home/会话 schema 版本标记与不兼容拒绝策略（bundled rc.x 操作被其他版本写过的 home 的安全边界）。

## Alternatives considered

- **维持私有 home**：落败——数据宇宙割裂违背上游 home/profile 模型，对 CLI 重度用户是硬伤；且与「插件管理即配置」的生态方向冲突。
- **Attach 已运行的 dsh web 实例**（ccgui 模式）：暂缓——版本漂移治理复杂、进程生命周期权威混乱，且破坏零环境默认；SDK `stdio` JSON-RPC（`packages/sdk/server`）作为更正规的通道后续单独评估。
- **file:// dist + IPC bridge 深嵌入**（`host/webserver` 注释点名的 Electron 形态）：远期记录——去掉 loopback HTTP 需自建 fetch/WS 桥，大改造非当下。

## Migration design（破坏性变更管控）

**实测 home 标准布局**（本项目私有 home，与上游语义一致）：`.credentials.yaml`、`settings.yaml`（无版本头字段）、`.dsh-web-port`、`profiles/`、`sessions/`、`storages/`、`.pnpm-store|.pnpm-cache`、`logs/`；**插件会在 home 根自建数据目录**（如 `dsh-pocket/`）——合并必须容忍未知目录，只搬已知项。

### 状态判定矩阵

| 前置状态 | 判定 | 动作 |
|---|---|---|
| A：无 `~/.dsh` | 目录不存在 | 整体迁入：sessions/storages/settings.yaml/credentials 搬入；创建 `profiles/desktop`；pnpm store 搬入（内容寻址安全）；旧 home **改名留备份不删除** |
| B：有 `~/.dsh` | 目录存在 | **加法合并，绝不覆盖既有文件**：仅新建 `profiles/desktop`；sessions 逐目录合并（同名 slug 进 `sessions.import-conflict-<ts>/` 隔离区）；settings.yaml/.credentials.yaml **一律保留现有**（我方副本留在备份）；storages 仅合并非冲突命名空间；store 复用现有 |
| C：版本偏斜 | home 由更新/更旧版本的 dsh 写过 | 默认**拒绝切换**并给出双逃生门：①提示升级桌面版 ②`DSH_HOME` 覆盖回私有模式。检测标记依赖 Stage 0 研究（settings.yaml 无版本头，需另找可靠信号，如 cordis.yml 结构指纹或上游版本文件） |

### 执行协议（对三种状态统一）

1. **Journal 幂等**：写 `<home>/migration-state.json` 记录步骤清单与完成位，中断后重跑自动跳过已完成步。
2. **copy → verify → rename-backup**：先复制、逐字节校验（sessions 文件数+大小），通过后才把源目录改名为 `<name>.migrated-<ts>.bak`（同文件系统瞬间完成，可手工整体回滚）。
3. **全程留痕**：每步写入 `logs/host.log` + 迁移专用日志；任何异常即停，保持半完成态由 journal 续跑。
4. **幂等重入**：迁移完成后再次启动检测到备份与标记即跳过。

### 分阶段灰度（默认值不一步翻转）

- **Stage 0 研究**：home 版本标记信号盘点（上游 settings/cordis 结构指纹）；session 文件格式与 slug 冲突语义；credentials/storages 实际布局全量盘点。
- **Stage 1**：本矩阵评审定稿（本 ADR 更新）。
- **Stage 2 实现**：`Services/HomeMigration/` 纯函数 planner + executor，状态×冲突注入的单测矩阵全覆盖；host.log 全程留痕。
- **Stage 3 opt-in**：默认仍私有 home，`DSH_DESKTOP_SHARED_HOME=1` 显式开启试运行。
- **Stage 4 翻转默认**：灰度稳定后默认共享，`DSH_DESKTOP_PRIVATE_HOME=1` 反向逃生门保留一个发布周期。
- **Stage 5 清理**：移除私有 home 创建路径（备份读取支持长期保留）。

## Risks

- 共享 home 无锁层：并发写同 profile 的 pnpm 安装存在竞态——专属 `desktop` profile 规避主要冲突面；home 级并发语义与 CLI 用户现状一致，依赖上游容忍度。
- schema 偏斜：bundled rc.x 与用户 CLI 版本差作用于共享 settings/sessions——前置研究未完成前不切默认。
- 存量迁移遗漏：一次性导入脚本需覆盖 credentials 与 workspace 映射。

## Acceptance criteria

- 默认 DSH_HOME 为 `~/.dsh`，首启创建 `profiles/desktop` 并装入 companion
- CLI 创建的会话在桌面 UI 可见（互通演示）
- dev 隔离守卫行为无回退
- 存量私有 home 有一键导入路径
- 版本偏斜策略有书面结论（允许/警告/拒绝矩阵）

## Related

- `implemented/process/2026-08-22-dev-runtime-isolation`：dev 隔离守卫保留，本提案仅改变产品态 home 归属。
- `implemented/feature/2026-08-22-companion-plugin-version-aware-upgrade`：版本感知比对逻辑不变，作用对象改为 `profiles/desktop`。
