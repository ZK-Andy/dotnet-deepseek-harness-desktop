# Agent Note: Ryn desktop shell bundling the complete DeepSeek Harness runtime

Status: proposed

## Problem

产品目标是 DeepSeek Harness 的 .NET 桌面端，且**开箱即用**：用户不应被要求预装 Node、DeepSeek Harness 运行时或任何环境。现有参照实现分别落在 Rust/Tauri（hairyf/deepseek-harness-desktop）与 TypeScript/Electron（op7418/pilot-harness），均非 .NET 栈。本仓库确定以 **C#/.NET** 构建（NuGet 命名空间 `DeepSeek.Harness.Desktop`），当前处于第二步起点，尚无任何 .NET 代码；需要一个钉死的宿主与运行时打包架构决策。

## Proposal

**宿主 = Ryn**（Yupmoh/Ryn，Tauri-for-C#）：C# 后端 + HTML/CSS/JS 前端，原生 OS WebView（WebView2/WKWebView/WebKitGTK），NativeAOT 自包含小体积，`[RynCommand]` 源码生成 IPC，`ryn.json` 能力沙箱（deny-by-default），内置 Shell/PTY 插件。

**完整运行时打包**（pilot-harness 思路）：随应用内置 Node 运行时 + `@deepseek-ai/dsh` + 桌面 profile + 插件 bundles + Web UI；**免用户安装任何外部环境**（对齐 pilot-harness "no separate installation required"）。

**运行模型**：
- Ryn 壳负责原生窗口、DSH 运行时子进程生命周期（启动/健康检查/崩溃恢复屏，借 Shell/PTY）、安装包；
- Ryn WebView 宿主渲染 dsh Web UI（CodePilot 式主题本身作为 dsh 插件加载）；
- 桌面增强一律做成 Harness 插件装入 profile，不做成桌面私有逻辑（插件树即应用运行时）。

**打包**：按平台自包含（NativeAOT 主程序 + 运行时作为应用资源），Windows / macOS / Linux。

## Alternatives considered

- **Electron + TypeScript（pilot-harness 路线）**：成熟、与参照实现同构、改造成本低。落败原因：产品明确要 C#/.NET 技术栈。
- **Tauri + Rust（hairyf 路线）**：极小、极快、有真实 deepseek-harness-desktop 先例。落败原因：后端强制 Rust，违背 C# 选择。
- **Photino（.NET 原生 WebView 薄壳）**：最接近的 .NET 选项。落败原因：无插件体系、无 IPC 源码生成、无能力沙箱、无脚手架 CLI——不足以承载复杂运行时生命周期管理。
- **.NET MAUI Blazor Hybrid / Avalonia**：落败：MAUI 重、移动优先、引入 XAML 宿主壳，非"自带 HTML/CSS/JS 前端"模型；Avalonia 是自渲染 XAML 控件而非 Web 宿主，无法直接承载 dsh Web UI。
- **不内置运行时（系统装 DSH + 浏览器直连 Web UI）**：实现最省。落败原因：违背"完整运行时打包/开箱即用"产品目标，与用户明确的 pilot-harness 思路冲突。

## Acceptance criteria

- Ryn 壳在至少一个平台启动并加载 dsh Web UI；
- 完整 DSH 运行时随应用内置，**无外部 Node/DSH 依赖**即可运行（对齐 pilot-harness 标准）；
- 壳管理运行时生命周期（启动/停止/崩溃恢复屏）；
- `DeepSeek.Harness.Desktop` 命名空间就位；MIT 许可；
- 撤销条件：Ryn 在目标平台能力验证失败（尤其 Linux WebKitGTK）→ 回到 Alternatives 重新评估。

## Risks

- **Ryn 为 Alpha**：Linux 部分能力待有焦点验证（notification、badge、global-shortcut、NativeAOT publish 为 🟡）。
- **内置 Node/运行时**：体积与许可（Node.js 许可）、架构矩阵（win-x64 / mac-arm64 / linux-x64）需打包期冻结；`DSH_HOME`/路径/环境变量在嵌入上下文需重定向验证。
- **`@deepseek-ai/dsh` 嵌入可行性**：运行时能否不改装即由壳托管，需先做技术侦察（见 02-8 前的前提）。
- **跨平台渲染差异**：各 OS WebView 引擎渲染有细微差异，属已知取舍。
