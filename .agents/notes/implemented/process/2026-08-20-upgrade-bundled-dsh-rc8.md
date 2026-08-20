# Agent Note: upgrade-bundled-dsh-to-rc8

Status: implemented

## Problem

项目把 `@deepseek-ai/dsh` 运行时完整闭包打进产物（`resources/runtime/`），运行版本由 `bundle-runtime-ci.sh` 的 `DSH_VERSION` 默认值钉住。此前为 `0.1.0-rc.7`（npm `latest` 标签）。上游 2026-08-19 发布了 `0.1.0-rc.8`（新于 rc.7，挂在 `next` 标签），用户要求整项目升级到 rc.8。

## Decision

**将内置 dsh 运行时升级到 `0.1.0-rc.8`**：

- `scripts/bundle-runtime-ci.sh` 的 `DSH_VERSION="${DSH_VERSION:-0.1.0-rc.7}"` → `0.1.0-rc.8`；`docs/architecture.md` 的说明同步为 `${DSH_VERSION:-0.1.0-rc.8}`。
- `bundle-runtime.sh` 是 `bundle-runtime-ci.sh` 的薄封装，自动继承新默认值；本地 `resources/runtime` 重新生成以便本地开发/验证也跑 rc.8（gitignore，CI 打包时按 `DSH_VERSION` 现取现打）。
- 运行时入口（`node_modules/@deepseek-ai/dsh/lib/bin.js`）与 `RuntimeLocator` 契约不变。

## Alternatives considered

- **维持 rc.7（官方 `latest`）**：落败——用户明确要求升 rc.8，且 rc.8 是上游更新版本；维持旧版会落后于上游修复/特性。
- **只改 CI、不动本地 bundle**：落败——"整个项目升级"含本地开发运行时，否则本地与发布闭包不一致（同 TRIM 教训）。
- **硬编码 rc.8 到各 workflow**：落败——`DSH_VERSION` 单点默认值即可，多处硬编码反而易漂移。

## Consequences

- 收益：全项目（发布闭包 + 本地运行时）统一到 rc.8，跟随上游 `next` 最新版。
- 代价/风险：rc.8 是 `next` 预发布标签（非 `latest`），可能有未稳定特性；依赖 `bundle-runtime-ci.sh` 的 `dsh web:` 自检与后续 CI 出包真实验证兜底。
- 验证：本地重新生成 `linux-x64` 闭包成功（`dsh: version 0.1.0-rc.8`，`@deepseek-ai/dsh` 解析为 `0.1.0-rc.8`），`dsh web:` 自检通过（396M）；正式包待下次 tag 触发 CI 后确认。
