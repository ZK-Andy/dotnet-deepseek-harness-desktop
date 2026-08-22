# Agent Note: upgrade-bundled-dsh-rc2

Status: implemented

## Problem

项目把 @deepseek-ai/dsh 运行时完整闭包打进产物（`resources/runtime/`），运行版本由 `bundle-runtime-ci.sh` 的 `DSH_VERSION` 默认值钉住，此前为 `0.1.1-rc.1`。上游已发布 `0.1.1-rc.2`（当前同时挂 latest 与 next 标签，较 rc.1 更新），用户要求整项目升级到 `0.1.1-rc.2`。

## Decision

将内置 dsh 运行时升级到 `0.1.1-rc.2`，全部引用点同步（单点默认值 + 三平台 workflow env + docs）：

- `scripts/bundle-runtime-ci.sh` 的 `DSH_VERSION` 默认值 `0.1.1-rc.1` → `0.1.1-rc.2`（单点默认值；本地 `bundle-runtime.sh` 为薄封装，自动继承）。
- `.github/workflows/package-{linux,macos,windows}.yml` 的 job env `DSH_VERSION: '0.1.1-rc.2'`（CI 构建闭包显式传值，漏改会让发布闭包停在 rc.1）；`dsh-closure-{rid}-{DSH_VERSION}-{NODE_VERSION}-v1` 缓存 key 随版本换新，各平台首个 run 全量重建（属预期）。
- `docs/architecture.md` 的 `@${DSH_VERSION:-0.1.1-rc.2}` 说明同步。
- pnpm 钉 11.7.0 **不动**：rc.2 无顶层 peerDependencies、依赖族全线 `^0.1.1-rc.2`（dsh-base/goal/skill/web-app/app-boot…）自洽；dshmarket 的 optional peer `dsh-settings@^0.1.0-rc.7` 在严格 node-semver 下仍可能命中同类解析问题，钉版继续保证构建确定性。

## Alternatives considered

- **维持 0.1.1-rc.1**：落败——用户明确要求升到 `0.1.1-rc.2`，且 rc.2 已是官方 latest+next，维持旧版落后于上游修复/特性。
- **只改 ci 脚本默认值、不碰 workflow env**：落败——CI 构建显式传 `DSH_VERSION`，漏改会让发布闭包仍用 rc.1，本地与 CI 产物不一致。
- **换 pnpm 版本/解 peer 区间的其他路线**：不采纳——钉版决策（`2026-08-21-pin-pnpm-1170-for-bundled-closure`）与 dsh 版本无关，本次升级不改变构建环境。

## Consequences

- 收益：全项目（发布闭包 + 本地运行时 + CI 三平台）统一到 `0.1.1-rc.2`，跟随上游 latest/next 最新版；rc.2 依赖族版本自洽，peer 解析风险低于 rc.1 时代。
- 代价/风险：仍是预发布版（rc 前缀），可能有未稳定特性；闭包缓存 key 换新带来的各平台 tag 首 run 全量构建（cache 按 ref 隔离，属预期）；正式包待下次 tag 触发 CI 复核。
- 验证：本地 `bundle-runtime.sh linux-x64` 重建闭包成功（dsh `0.1.1-rc.2`，`dsh web:` 自检通过，`.bundle-meta.json` 记 `dshVersion:0.1.1-rc.2`）；CI 三平台待 workflow_dispatch 预跑复核。

## Related

- `2026-08-21-upgrade-bundled-dsh-011-rc1`：同机制上次升级（rc.8→rc.1），本次为版本延续；其版本事实为历史快照，引用点清单与本次一致。
- `2026-08-21-pin-pnpm-1170-for-bundled-closure`：pnpm 钉版决策，本次不涉及变更。