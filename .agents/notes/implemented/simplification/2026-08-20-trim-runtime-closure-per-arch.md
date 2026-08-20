# Agent Note: trim-runtime-closure-per-arch

Status: implemented

## Problem

`resources/runtime` 闭包体积大（本地 linux 396M，Windows CI 更大 ~1.5GB），拖慢三处：捆绑时 `cp -Lr` 拷贝、Inno Setup `LZMA` 压缩安装器、安装体积/上传。历史曾有 `TRIM` 盲删（`*.md/.ts/.map/__tests__`），因"低收益高风险、可能埋潜伏运行时故障"被用户否决移除。现需在不碰高风险源码的前提下做**安全的 per-arch 瘦身**。

## Decision

**在 `bundle-runtime-ci.sh` 的 `[3/3]` 拷贝后、`[4/4]` 自检前，新增 `trim_runtime_closure()`，只剪三类无风险产物**：

- **node-pty 非当前平台 `prebuilds/*` 目录**：node-pty 把所有平台 prebuild（`win32-x64/arm64`、`darwin-x64/arm64`、`linux-x64/arm64`）当普通文件随包；运行时按 `process.platform+arch` 只选当前平台目录，删其它平台目录绝对安全。映射：`linux-x64→linux-x64`、`linux-arm64→linux-arm64`、`win-x64→win32-x64`、`osx-x64→darwin-x64`、`osx-arm64→darwin-arm64`。
- **`*.map` 源码映射**：仅调试用，运行时不被加载。
- **`README/CHANGELOG/CONTRIBUTING/HISTORY *.md`**：纯文档。

**明确不剪**：`.ts`/`.d.ts` 源码、LICENSE——正是历次盲删 TRIM 的高风险部分。裁剪在 `[4/4]` 自检前执行，**Linux 强自检（`dsh web:` 必须给出 URL）验证裁剪后闭包仍可启动**，失败即 `fail loud`。

## Alternatives considered

- **恢复盲删 TRIM（*.ts/*.d.ts/*.map/__tests__）**：落败——用户已明确否决（运行时故障风险、收益低）；`.d.ts`/`.ts` 可能被路径 exports 或运行时解析引用，风险不可控。
- **只剪原生跨平台、不碰任何 JS 文件**：可行且最稳，但漏掉 ~35MB sourcemap 的确定收益；本方案以"自检把关"纳入 sourcemap/md，形成可控增量。
- **per-arch 只在打包时临时剪、不动 resources/runtime 原件**：落败——闭包最终仍入包，运行时要的是完整；不如在组装时就剪好（同一份给本地/CI）。

## Consequences

- 收益（本地实测）：linux 396M → **339M**（−14%）；`dsh web:` 自检通过；Windows CI 闭包预期同比缩小（sourcemap/md/多平台 node-pty 都在）。
- 代价/风险：失去闭包内 sourcemap（调试桌面内嵌 JS 需另备 debug 包）；node-pty 若未来改 prebuild 目录命名需同步映射。其它平台（win/mac）无 Linux 强自检兜底，但裁剪仅删"非当前平台"与纯文档/sourcemap，风险趋近于零。
- 验证：本地 `bundle-runtime.sh linux-x64` 全链路（trim→自检→meta）通过；下次 tag 三平台 CI 实测体积与 Windows「打包」时间。
