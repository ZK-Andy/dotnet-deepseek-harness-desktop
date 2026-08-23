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
