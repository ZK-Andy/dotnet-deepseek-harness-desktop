# Agent Note: companion-install-path-dsh（companion 安装漏 PATH-dsh 运行时路径）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

v0.4.0 实机回归（用户应用内自更新 0.3.12→0.4.0 后重启）：**桌面自身插件（companion）未安装**。

`~/.dsh/logs/host.log` 实锤：

- `[bootstrap] PATH dsh 可用（0.1.1-rc.2），跳过首启引导`——用户机器有 PATH dsh，不进入引导；
- `[host] 桌面 profile reconcile：移除不可解析插件引用 dsh-desktop-companion（file:/usr/lib/.../resources/runtime/dsh-desktop-companion.tgz）`——reconcile 删掉了指向**旧捆绑位置** `resources/runtime` 的 companion 引用（online-first 后该目录已退役）；
- 之后**没有任何** `随包插件安装…` 的行——无人在新位置 `resources/plugins` 补装 companion。

**根因**：companion 安装（`EnsureBundledPluginsBeforeSpawnAsync`）的两个执行点都要求特定的运行时形态：

- call site 1（Program.cs，非引导路径）门控 `!bootstrapNeeded && bundledClosure is not null`——要求**捆绑闭包**存在；
- call site 2（引导后台任务内）只在**引导**路径运行。

而 online-first 后**捆绑闭包已退役**（`bundledClosure` 恒为 null），用户有 PATH dsh 时既不引导、也无闭包 → **两个执行点都不触发** companion 安装。reconcile 又清理了旧的 companion 引用 → companion 从 profile 消失、无按新位置重装。这是 online-first 去捆绑 + batch-1「spawn 前装」组合引入的回归。

## Decision

**companion 安装覆盖所有「运行时已就位」形态（捆绑闭包或 PATH-dsh），不再要求捆绑闭包存在。**

1. `EnsureBundledPluginsBeforeSpawnAsync` 的 `nodeExe`/`dshEntry` 改可空：`null` 时 BuildPsi 用 PATH 上的 `dsh` 命令（`FileName="dsh"`、无 bin.js 入口参数），等价宿主 `HarnessRuntimeHost` spawn 时的 `dsh` 命令解析；非 null 时保持 `node <dshEntry>` 现状。
2. Program.cs call site 1 门控由 `!bootstrapNeeded && bundledClosure is not null && !devIsolation` 放宽为 `!bootstrapNeeded && !devIsolation`（`!bootstrapNeeded` 即保证运行时已就位：捆绑闭包或 PATH dsh）；实参 `bundledClosure?.NodeExe` / `bundledClosure?.DshEntry`（PATH-dsh 时为 null）。
3. dev 显式覆盖共享 home 仍跳过（防串扰），`!(isDev && !devAutoIsolated)` 语义不变。

reconcile 仍正确（清理死引用），本次修复补齐「清理后按新位置重装」的缺口。

## Alternatives considered

- **为 PATH dsh 解析出 nodeExe/dshEntry**：PATH dsh 只是 PATH 上的 `dsh` 命令（宿主 spawn 即 `FileName="dsh"`），无稳定、可靠的「node + bin.js」二元组可解析（npm 全局 `dsh` 是 shebang 包装/符号链接，跨平台形态不一）。直接复用宿主 spawn 的 `dsh` 命令解析最一致。落败。
- **只改 reconcile 不重装**：reconcile 的职责就是清死引用，清理本身正确；缺的是清理后的**重装**（companion 现由安装器资源 `resources/plugins` 供给）。只清不装仍缺 companion。落败。
- **放宽 call site 1 但保持非空 nodeExe/dshEntry**：PATH-dsh 无该二元组，无法非空。落败；改可空 + `dsh` 命令回退。

## Consequences

- online-first 升级存量用户（或任何 PATH-dsh 运行时）首启即恢复 companion 安装，配合 reconcile 在启动序列中「清旧引用 → 按新位置重装」闭环。
- companion 安装保持 best-effort（失败只留日志、不阻断 dsh 启动，下次自愈），语义不变。
- 回归测试：`EnsureBundledPluginsBeforeSpawn_PathDsh_UsesDshCommand_WhenNoBundledRuntime`（断言 null 时 `FileName="dsh"`、无 bin.js 入口参数、companion 写回 bundles）；既有 bundled 测试保持 `node <dshEntry>` 形态。

## Related

- [online-first-unbundled-runtime](../architecture/2026-08-29-online-first-unbundled-runtime.md)：去捆绑（`resources/runtime` 退役）是 companion 引用失效的上游诱因。
- [reference-alignment 批次一](../architecture/2026-08-29-reference-alignment.md)：companion 改 spawn 前安装（batch-1）——其执行点未被 PATH-dsh 覆盖，本 ADR 补齐。
