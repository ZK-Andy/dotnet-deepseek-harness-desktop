# Agent Note: Ryn desktop shell bundling the complete DeepSeek Harness runtime

Status: implemented

## Problem

产品目标是 DeepSeek Harness 的 .NET 桌面端，且**开箱即用**：用户不应被要求预装 Node、DeepSeek Harness 运行时或任何环境。现有参照实现分别落在 Rust/Tauri（hairyf/deepseek-harness-desktop）与 TypeScript/Electron（op7418/pilot-harness），均非 .NET 栈。本仓库确定以 **C#/.NET** 构建（NuGet 命名空间 `DeepSeek.Harness.Desktop`），需要一个钉死的宿主与运行时打包架构决策。

## Decision

**宿主 = Ryn**（Yupmoh/Ryn，Tauri-for-C#；版本随 csproj 包引用，现为 0.32.0 带 `Ryn.Callbacks` 导航回调）：net10.0 C# 后端 + 系统 WebView（WebView2/WKWebView/WebKitGTK），`Ryn.Ipc` 源码生成命令路由，`ryn.json` 能力沙箱 deny-by-default；发布为 JIT（csproj `PublishAot=false`，见 2026-08-28-publish-aot-jit-alignment）。

**完整运行时打包**（pilot-harness 思路）：`resources/runtime/` 内置 Node 二进制 + 钉版 `@deepseek-ai/dsh` 闭包（整树 node_modules）+ `dshmarket.tgz` + `dsh-desktop-companion.tgz`；`RuntimeLocator` 优先捆绑、缺失回退 PATH dsh（开发形态）。用户零外部环境依赖。

**运行模型**：
- 壳负责窗口与子进程生命周期：`HarnessRuntimeHost` spawn → 解析 `dsh web:` URL → WebView 加载；`RuntimeSupervisor` 崩溃恢复屏 + 自动重启 + 稳定端口保 origin（崩溃后回原会话）。
- 桌面增强一律做成 Harness 插件装入 profile（dshmarket、dsh-desktop-companion），不做成桌面私有逻辑——插件树即应用运行时。

**打包**：三平台自包含产物——Linux x64/arm64 deb+rpm、macOS arm64/x64 dmg、Windows x64 setup.exe，统一 `release.yml` 聚合发布；v0.2.x 起全链路实测（tag 触发 → 四流水线 → 单 Release + SHA256SUMS）。

## Alternatives considered

- **Electron + TypeScript（pilot-harness 路线）**：成熟、与参照实现同构、改造成本低。落败原因：产品明确要 C#/.NET 技术栈。
- **Tauri + Rust（hairyf 路线）**：极小、极快、有真实 deepseek-harness-desktop 先例。落败原因：后端强制 Rust，违背 C# 选择。
- **Photino（.NET 原生 WebView 薄壳）**：最接近的 .NET 选项。落败原因：无插件体系、无 IPC 源码生成、无能力沙箱、无脚手架 CLI——不足以承载复杂运行时生命周期管理。
- **.NET MAUI Blazor Hybrid / Avalonia**：落败：MAUI 重、移动优先、引入 XAML 宿主壳，非"自带 HTML/CSS/JS 前端"模型；Avalonia 是自渲染 XAML 控件而非 Web 宿主，无法直接承载 dsh Web UI。
- **不内置运行时（系统装 DSH + 浏览器直连 Web UI）**：实现最省。落败原因：违背"完整运行时打包/开箱即用"产品目标，与用户明确的 pilot-harness 思路冲突。

## Consequences

- 原验收条件逐项闭环：真实桌面启动并加载 Web UI（Linux rpm 实机多轮验收含自更新循环）；零外部 Node/DSH 依赖运行；生命周期管理就位；命名空间与 MIT 就位。撤销条件（Ryn 在 Linux WebKitGTK 能力失败）未触发。
- Ryn 为 Alpha 的能力边界仍在：notification/badge/global-shortcut 等未采用（桌面增强走插件路线绕开）；跨平台 WebView 渲染差异属已知取舍。
- Node 许可与架构矩阵在 `bundle-runtime-ci.sh` 单点冻结（DSH_VERSION/NODE_VERSION 变更同点升级）；闭包体积换确定性是在案差异化（「零下载确定性」）。
- 「嵌入上下文的 DSH_HOME 重定向」开放问题已由 [shared-home-desktop-profile](2026-08-23-shared-home-desktop-profile.md) 回答：数据 home 采用共享 `~/.dsh`（B 形态），随包闭包保留为预览期形态。

## Related

- [shared-home-desktop-profile](2026-08-23-shared-home-desktop-profile.md)：数据 home 形态的现行裁决；与本决定的「闭包随包」同向互补（数据共享一份 + 执行副本随包）。
- [dev 运行时隔离](../process/2026-08-22-dev-runtime-isolation.md)：dev 判定依赖「有无捆绑闭包」信号——闭包保留故信号有效；远期去捆绑须先重构该判定。
- [online-first 去捆绑运行时](../../proposed/architecture/2026-08-29-online-first-unbundled-runtime.md)（proposed）：**部分取代本篇**——「完整运行时打包」决定由该 ADR 转向（安装器瘦身 + 首启引导），捆绑闭包与零下载确定性表述随 offline 约束删除而退役。
