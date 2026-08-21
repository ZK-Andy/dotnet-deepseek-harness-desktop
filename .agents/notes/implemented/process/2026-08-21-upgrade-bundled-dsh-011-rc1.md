# Agent Note: upgrade-bundled-dsh-to-011-rc1

Status: implemented

## Problem

项目把 @deepseek-ai/dsh 运行时完整闭包打进产物（resources/runtime/），运行版本由 bundle-runtime-ci.sh 的 DSH_VERSION 默认值钉住，此前为 0.1.0-rc.8（官方 next 标签，2026-08-19 发布）。上游已发布 0.1.1-rc.1（当前同时挂 latest 与 next 标签，较 rc.8 更新），用户要求整项目升级到 0.1.1-rc.1。

## Decision

将内置 dsh 运行时升级到 0.1.1-rc.1，全部引用点同步：

- scripts/bundle-runtime-ci.sh 的 DSH_VERSION 默认值 0.1.0-rc.8 → 0.1.1-rc.1（单点默认值）。
- .github/workflows/package-{linux,macos,windows}.yml 的 env.DSH_VERSION 同步（闭包缓存 key dsh-closure-{rid}-{DSH_VERSION}-... 随之自动换新，避免旧缓存误命中）。
- docs/architecture.md 的 @${DSH_VERSION:-0.1.1-rc.1} 说明同步。
- bundle-runtime.sh 是 bundle-runtime-ci.sh 的薄封装，自动继承新默认值；本地 resources/runtime 重新生成（.bundle-meta.json 签名不匹配触发全量重建，走 dsh web: 自检），保证本地开发与发布闭包同版本（同 rc8 升级先例）。
- 运行时入口（node_modules/@deepseek-ai/dsh/lib/bin.js）与 RuntimeLocator 契约不变。

## Alternatives considered

- 维持 0.1.0-rc.8：落败——用户明确要求升到 0.1.1-rc.1，且 0.1.1-rc.1 已是官方 latest+next，维持旧版落后于上游修复/特性。
- 只改 CI 默认值、不同步 workflow env：落败——workflow 显式注入 DSH_VERSION 会盖过脚本默认值，不同步会导致 tag 出包仍用 rc.8（且缓存 key 不变误命中旧闭包）。
- 只改默认值、不重新生成本地闭包：落败——与 rc8 先例一致，本地与发布的闭包不一致会掩盖卸载层面的问题。

## Consequences

- 收益：全项目（发布闭包 + 本地运行时 + CI 三平台）统一到 0.1.1-rc.1，跟随上游 latest/next 最新版。
- 代价/风险：0.1.1-rc.1 仍是预发布版（rc 前缀），可能有未稳定特性；依赖 bundle-runtime-ci.sh 的 dsh web: 自检、闭包缓存 key 换新带来的 tag 首 run 全量构建（cache 按 ref 隔离，属预期）、以及后续 CI 出包真实验证兜底。
- 验证：本地重新生成 linux-x64 闭包成功（dsh 0.1.1-rc.1，dsh web: 自检通过）；正式包待下次 tag 触发 CI 后确认。
