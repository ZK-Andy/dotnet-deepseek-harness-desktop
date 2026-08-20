# Agent Note: cache-assembled-runtime-closure

Status: implemented

## Problem

Windows 打包最重的两步之一是「生成捆绑运行时」（~277–310s）。前一次尝试缓存 pnpm store（`2026-08-20-pnpm-store-ci-cache`）被证实**对捆绑无效**：即使缓存命中，捆绑仍 ~283s，因为真瓶颈是 `cp -Lr` 拷 ~1.5GB `node_modules` 闭包（pnpm 解析/原生重编不是瓶颈）。需要直接缓存「组装好的闭包」本身。

## Decision

**缓存组装后的 `resources/runtime` 整闭包，命中即整步跳过**：

- `bundle-runtime-ci.sh` 顶部新增**闭包缓存跳过**：若 `$DEST/.bundle-meta.json` 存在且与本次请求一致（`dshVersion`/`nodeVersion`/`platform` 三字段匹配）且入口存在 → 打印命中并 `exit 0`；不匹配则打印并全量重建。
- 成功构建末尾写入 `.bundle-meta.json`（签名三字段）。
- 三平台 `package-*.yml` 新增「缓存 resources/runtime 闭包」：`path: resources/runtime`，key `dsh-closure-${{matrix.rid}}-${{env.DSH_VERSION}}-${{env.NODE_VERSION}}-v1`、restore-keys `dsh-closure-${{matrix.rid}}-`；job env 增 `DSH_VERSION`/`NODE_VERSION`（也即 `bundle-runtime-ci.sh` 的 `$DSH_VERSION`/`$NODE_VERSION` 单一来源）。
- `*.map`/README 等由同批 `trim-runtime-closure-per-arch` 先剪小，闭包缓存更轻、恢复更快。

## Alternatives considered

- **只缓存 pnpm store（上一方案）**：落败——实测命中后捆绑仍 ~283s，瓶颈是 `cp -Lr` 闭包而非 pnpm 解析；store 缓存仅省 re-save，被 `2026-08-20-pnpm-store-ci-cache` 记录并在 Consequences 修正。
- **不缓存、仅靠瘦身**：落败——瘦身只降体积不降"每 run 全量重建"的常数开销；缓存直接消灭整步。
- **用 `git rev-parse`/hash 当键**：落败——闭包内容由 DSH_VERSION/NODE_VERSION/arch 决定，用显式三字段当键清晰且可被 `.bundle-meta.json` 复核，避免每次无谓重建。

## Consequences

- 收益（本地实测）：重建后二次运行跳过，`bundle-runtime.sh` 仅 **0.13s**；对 Windows「生成捆绑运行时」~280s 是根治级削减（命中即近零）。
- 代价/风险：**GitHub Actions 缓存按分支/ref 作用域隔离**——tag 触发发布流每个 tag 是独立 ref，缓存跨 tag 可能不命中（需同 ref 复跑或默认分支共享才命中）；闭包 ~0.3–1.3GB 的 cache 恢复/保存有成本；缓存命中时跳过自检（信任缓存来自已验证构建）。
- 验证：本地全链路（trim→自检→写 meta→二次跳过 0.13s）通过；CI 需下次 tag/复跑实测 Windows 捆绑与总时长。
