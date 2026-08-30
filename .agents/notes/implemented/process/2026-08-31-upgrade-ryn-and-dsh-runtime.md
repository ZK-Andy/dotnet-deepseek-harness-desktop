# Agent Note: upgrade-ryn-and-dsh-runtime

Status: implemented

## Problem

上游 Ryn 与 dsh 均发布新版并已发包：

- **Ryn（NuGet）**：0.32.0 之后一日连发 `0.32.1`/`0.33.0`/`0.34.0`/`0.35.0`/`0.35.1`，最新 `0.35.1`（2026-08-30 08:14Z）；GitHub `v0.35.1` = Latest。项目钉 `0.32.0`（Ryn / Ryn.Plugins.Tray / Ryn.Callbacks / Ryn.Ipc.Generator 四包）。
- **dsh（npm）**：`@deepseek-ai/dsh` 的 `alpha` dist-tag 发布 `0.1.2-alpha.2`（2026-08-30 14:10Z）；`latest`/`next` 仍为 `0.1.1-rc.2`。项目运行时 `RuntimeBootstrapOptions.DshSpec` = `@deepseek-ai/dsh@latest`（=0.1.1-rc.2），`RuntimeVersionGate.MinimumVersion` = `0.1.1-rc.2`。

用户拍板「直接升级到最新版」：Ryn 升 `0.35.1`；dsh 吃到 `0.1.2-alpha.2` 并同步抬 RuntimeVersionGate 底线。

## Decision

### Ryn 0.32.0 → 0.35.1

- csproj 四包（Ryn / Ryn.Plugins.Tray / Ryn.Callbacks / Ryn.Ipc.Generator）及传递的 Ryn.Core/Ryn.Interop 统一升到 `0.35.1`。
- `0.32.0→0.35.1` 的提交全为 Linux 渲染/Wayland 增量特性（共享内存渲染、显示后端选择、Wayland 放置、窗口移动事件），无 C#/导航回调/源生成器 API 破坏迹象；以 bump 后 build 0 警告 + test 全绿为兼容性实证。
- **Ryn.Callbacks 源生成器在 0.35.1 仍产 `public` 无文档符号**（`RynNavigationCallbacksRynExtensions`）——`ide0005-enforce-via-format-gate` 的「主工程不开启 `GenerateDocumentationFile`」结论不因本次 bump 改变。

### dsh 运行时钉 `0.1.2-alpha.2`

- `RuntimeBootstrapOptions.DshSpec`（代码默认 + `appsettings.json`）`@deepseek-ai/dsh@latest` → `@deepseek-ai/dsh@0.1.2-alpha.2`（**钉版**，非跟 `@alpha` tag）。
- `RuntimeVersionGate.MinimumVersion` `0.1.1-rc.2` → `0.1.2-alpha.2`。
- 理由：alpha 唯一实质变化 = 一次性 Token 鉴权取代 ApiProxy（协议级变更）；钉到具体 `0.1.2-alpha.2` 使全新安装确定性获得该版本，且与「底线 = `0.1.2-alpha.2`」同源一致。
- **重入 `@latest` 条件**：npm `latest` dist-tag 升至稳定 `0.1.2`（或更高）时，将 `DshSpec` 回退 `@deepseek-ai/dsh@latest` 并重估底线——恢复 online-first「内核升级与壳发版解耦」策略。

## Alternatives considered

- **dsh 改 `@deepseek-ai/dsh@alpha`（跟 alpha tag）**：保留「跟 dist-tag」形态，但会随未来 `alpha.3+` 漂移——用户只审过 alpha.2，且与「升级到 alpha.2」的确定性诉求不符（非确定目标）。落败。
- **维持 dsh `@latest` 不动**：实际无升级（latest 仍 `0.1.1-rc.2`），不满足「吃到 alpha.2」。落败。
- **只抬 `RuntimeVersionGate.MinimumVersion`、不动 `DshSpec`**：底线口径与目标运行版本不一致（提示永远指向「桌面支持更低版本」），且不改变首装实际获取的版本。落败。
- **Ryn 维持 0.32.0**：落后上游修复/特性，且上游需求点（Linux/Wayland 渲染）对本仓 Linux WebKitGTK 壳有实际价值。落败。

## Consequences

- **Ryn 0.35.1**：壳 WebView / 托盘 / 导航回调 / IPC 全链保持 0.32.0 行为（增量特性为主）；build 0 警告、test 通过。
- **dsh pin alpha.2**：`DshSpec` 仅在**全新安装**（无捆绑运行时且无 PATH dsh）首启引导一次性生效——装 `0.1.2-alpha.2`；已装 / 复用 PATH dsh 的存量机器走 REUSE 路径，不受影响。
- **底线 `0.1.2-alpha.2`**：运行 dsh < `0.1.2-alpha.2`（如 `0.1.1-rc.2`）的机器启动时触发版本底线横幅（提示升级/用桌面运行时），不阻断、不自动迁移（RuntimeVersionGate 语义不变）。注意：本地 PATH dsh 为 `0.1.1-rc.2` 的开发机将看到该横幅。
- **实机验收转交**：alpha token 鉴权——`dsh web:` 带 `?token=` 握手 + 跨进程冷启动 session 保持，须真机验证（本地沙箱无显示/网络）。转 HANDOFF 待办。
- **测试**：`RuntimeBootstrapOptionsTests` 默认 `DshSpec` 断言、`RuntimeVersionGateTests` 底线比较用例随之更新（`0.1.1-rc.x` 由「不低于」改为「低于」）。

## Related

- [online-first-unbundled-runtime](../architecture/2026-08-29-online-first-unbundled-runtime.md)（implemented）：本 ADR 临时取代其「dsh：`@latest`」版本策略行（钉 alpha.2；重入条件见 Decision）。
- [ide0005-enforce-via-format-gate](2026-08-31-ide0005-enforce-via-format-gate.md)（implemented）：Ryn.Callbacks 生成器 public 符号文档缺口在 0.35.1 仍存，本 ADR 不改变其结论。
