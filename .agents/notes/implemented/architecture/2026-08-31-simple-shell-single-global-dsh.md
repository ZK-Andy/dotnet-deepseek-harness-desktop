# Agent Note: simple-shell-single-global-dsh

Status: implemented

## Problem

早期"自带运行时/闭包"模型虽有转向 online-first，但转向**不彻底**：桌面仍维护一份**独立的下载运行时**（`~/.dsh-desktop/runtime`），与用户自装的 CLI dsh 并存，两者共用同一 `~/.dsh` 共享 home，存在**版本分叉 → 会话数据不兼容**的隐患（历史上出现过）。用户在讨论中明确：我们要的是**一台机器只有一份 dsh**，桌面依赖它，不要自己的运行时。

## Decision

**桌面就是一个简单壳，依赖系统全局 node（PATH）+ 全局 dsh（`npm install -g @deepseek-ai/dsh@alpha`），桌面与终端共用这一套。**

- **node 走系统全局**：桌面用**用户 PATH 上那份全局 node/npm**，经它的 `npm` 执行 `npm install -g @deepseek-ai/dsh@alpha`，把 **dsh 装到系统全局位（PATH 上）**。桌面 + 终端共用**同一套全局 node + 全局 dsh** → 只有一份，无版本分叉。
- **确保它有**：桌面启动检测全局 `dsh` 版本——没有 / 落后 → 装/更新到 `@alpha`；已最新则直接用。**显式宣告**：桌面对用户全局 dsh 的 `npm install -g @deepseek-ai/dsh@alpha` 是"装/更新到 @alpha 通道"——当用户自装 dsh 低于兼容底线（`RuntimeVersionGate.MinimumVersion`）时，桌面会在启动引导中**自动把其全局 dsh 升级到 @alpha**；这是有意的行为（避免与 CLI 分叉），非用户可选的"只提示不动作"。
- **node 检查/下载一次确保**：node 检查/下载只在"需要确保 dsh"时做（壳启动/更新时判定），不反复下载、不重复联网；用户有系统 node 就用。
- **没 node 时桌面主动装 node 到系统全局**：无系统 node/npm 时，桌面**下载最新官方 node 发行包**（nodejs.org dist，保留基本完整性校验/重试——SHA256 摘要先行 + 官方/镜像多源回落），解压并**装到系统全局可被 PATH 找到的位置**（默认用户可写且已在 PATH 的 `~/.local`，也可配 `/usr/local`），并把该 node 的 bin 暴露进**宿主 spawn PATH 与终端 PATH**（桌面与终端共用同一份 node）。**不是**放到桌面的私有目录再单独接。要 sudo（写系统位如 `/usr/local`）时，提示用户手动执行安装命令（官方安装方式/解压到目标位置），呈现在引导错误态，不静默失败。
- **dsh 权限不足提示**：装好 node 后用它 `npm install -g @alpha` 装**全局 dsh**（落到系统全局 PATH 语义）；dsh 因权限需 sudo（如系统 node 的 npm 全局位在 `/usr/local`）时，不静默失败——提示用户在终端手动执行 `sudo npm install -g @deepseek-ai/dsh@alpha`。
- **删除独立运行时**：不再有 `~/.dsh-desktop/runtime`、不再"下载 Node 装到桌面自备私有目录"；dsh 探测直接走 PATH（`RuntimeVersionGate.ProbeAsync`，无独立 `RuntimeLocator`）。
- **`RuntimeVersionGate.MinimumVersion` 保留**：全局 dsh 低于兼容线时提示（更新失败/用户自装极旧版的兜底）。
- **代价（已拍板）**：依赖全局 node + 全局 dsh；上游预发布（alpha）breaking 直触达；装/更新需联网且可能耗时；无 node 机器需先装 Node（提示用户）。

## Alternatives considered

- **保留独立下载运行时（`~/.dsh-desktop/runtime`，在线优先原状）**：落败——与用户 CLI 双份并存，共享 home 上版本分叉 → 会话不兼容隐患；且引入"有则复用/无则下载"的分叉逻辑，复杂。
- **桌面直接管用户全局这份、但不自给 node（B1a）**：落败——全新机无 node/npm 时无法 `npm install -g`，壳拒绝启动；不满足"没有就装"。
- **下载 node 到桌面私有目录 + 暴露私有 PATH（B2 私有序）**：落败——等于再造一份"桌面管理的 node/PATH"，违背"全机一份、都全局"的模型；node 必须走系统全局，否则桌面与终端 node 分叉。
- **node 为系统全局安装（采纳，即"全局 B2"）**：采纳——dsh 是 Node 包、`dsh` 靠 `env node` 运行，无系统 node 时桌面必须保证有 node 才能让那份 dsh 跑起来；node 走**系统全局安装**（PATH 上用户那份，或桌面下载官方 node 并**装到系统全局前缀**、要 sudo 则提示用户手动执行命令），**为唯一 dsh 提供/确保全局 node**，而非"多一份桌面私有运行时"。（桌面与终端共用全局 node+dsh，无版本分叉。）
- **dsh 仍钉版 / 跟 `@latest`**：落败——上游现直发预发布包，`@latest` 滞后；钉版又回到"桌面与用户版本可再分叉"。跟随 `@alpha` 通道最贴合"跟随最新"。

## Consequences

- **结构简化**：移除 `RuntimeBootstrap` 的"下载 Node → 装 own-runtime/私有 NodeToolDir"主机、`RuntimeLocator` 的下载运行时路径、`~/.dsh-desktop/runtime`、桌面私有 node/PATH；引导进度页用于"确保全局 dsh"的过程（检测/全局 node/装或更新）。
- **一份 dsh + 一份全局 node**：桌面与终端共用系统全局 node + 全局 dsh → 无版本分叉、无会话不兼容。
- **node 系统全局**：桌面用全局 node 的 npm 把 dsh 装到系统全局 PATH；无系统 node 时桌面下载最新官方 node 并**装到系统全局前缀**（默认 `~/.local`、也可配 `/usr/local`，写系统位需 sudo 时提示手动命令），不自备私有 node。
- **node 一次确保**：node 检查/下载只在需要确保 dsh 时执行一次（不反复联网）；有系统 node 或全局前缀已装即复用，缺了才下载装。
- **权限提示**：`npm install -g @alpha` 因权限（EACCES/EPERM）失败时，引导错误态呈现手动命令 `sudo npm install -g @deepseek-ai/dsh@alpha`；node 装到系统全局位权限不足时提示用户以管理员安装 Node.js。
- **README/ADR 重述**：从"零环境/自包含/own-runtime、桌面提供私有 node"改述为"依赖系统全局 node + 全局 dsh、简单壳；无则装、有则更新到 `@alpha`；无 node 则桌面装 node 到系统全局（需 sudo 给手动命令）"。
- **测试**：`RuntimeBootstrap`/`RuntimeLocator`/`CliShim` 相关用例随之改写；新增"全局 node 复用 / node 系统全局安装 + 下载 / node 权限提示 / sudo 提示"路径。
- 验证：build 0 警告、test 全绿、门禁全绿、三重审核收口。

## Related

- [online-first-unbundled-runtime](../architecture/2026-08-29-online-first-unbundled-runtime.md)（implemented）：本篇取代其"下载自备运行时（`~/.dsh-desktop/runtime`）"的机制表述；"跟随 @latest 解耦"一句最终收敛为"跟随 `@alpha` 预发布通道"。
- [shared-home-desktop-profile](../architecture/2026-08-23-shared-home-desktop-profile.md)（implemented）：共享 home 不变；本篇取消"桌面自备运行时"这一会造成与 CLI 分叉的来源。
- [reference-alignment](../architecture/2026-08-29-reference-alignment.md)（implemented）：本篇部分取代其"CLI shim / PATH 注册"批次——dsh 已全局在 PATH（桌面不再生成 dsh shim，仅 pnpm shim），node 亦走系统全局并暴露到 PATH；双向见该篇的回链。
- [upgrade-ryn-and-dsh-runtime](../process/2026-08-31-upgrade-ryn-and-dsh-runtime.md)（implemented）：本篇**取代其"dsh 钉 `0.1.2-alpha.2`（非跟 @alpha tag）"版本通道决定**——dsh 现跟随 `@alpha` 预发布通道（不钉版）；其"重入 @latest 条件"一句同样由本篇收敛。详见该篇本 ADR 回链。
