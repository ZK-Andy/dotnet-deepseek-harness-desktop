# DeepSeek Harness Desktop for .NET

中文 | [English](README.en.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![build & test](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml/badge.svg)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml)
![platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-4f6ef7)
![.NET](https://img.shields.io/badge/.NET-net10.0-512bd4)

**[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（MIT）的 .NET 桌面客户端**，基于 [Ryn](https://github.com/Yupmoh/Ryn)（Tauri-for-C# 原生 WebView 框架）。桌面壳**内置完整 DeepSeek Harness 运行时**——终端用户**无需单独安装 Node / DeepSeek Harness**。

> 现状：`v0.1.12`（`12/12` 单测 + 门禁全绿，`CI` 双绿）——原生壳 + 内置运行时 + 崩溃恢复 + 插件市场已闭环；`Wayland`/`图标` 已正，`0.1.11` 起市场随包 `497K` 真包 + 后台 `JSON` 检测/迁移/`allowBuilds` 自愈，`0.1.12` 后台装完自动重启即现。

## 亮点

- **原生轻量壳**：C# 后端 + HTML/CSS/JS 前端，跑在系统 WebView（WebView2 / WKWebView / WebKitGTK），NativeAOT 就绪，能力沙箱 deny-by-default（`ryn.json`）。
- **完整运行时内置**：`resources/runtime/` 内置 Node 二进制 + `@deepseek-ai/dsh` 依赖闭包（`pilot-harness` 整树 `node_modules` 模型，`--store-dir` 规避 `sqlite` 锁）；壳以私有 `DSH_HOME` 拉起 `dsh web`，UI 加载 `dsh web:` URL。无 PATH `dsh`/`node` 也能跑（缺内置时回退 PATH dsh）。
- **崩溃恢复**：壳监督运行时子进程——退出即显示恢复屏、重启子进程、把同一窗口导航到新 URL；**端口保持稳定**，Web UI 的 origin（及页面级会话记忆）在重启后存活——**崩溃后回到之前对话**。
- **插件市场预装**：`dsh-market`（`https://github.com/dsh-market/dsh-market`）已随包 `497K` 真包到 `resources/runtime/dshmarket.tgz`——首启后台 `file://` 静默安装到 `DSH_HOME`（`System.Text.Json` 精确检测、`app` 假依赖迁移、`pnpm-workspace.yaml 6` 项 `allowBuilds` 自愈），装完由 `RuntimeSupervisor` 重启即现，`1200+` 插件可搜一键装（`0.1.8` 阻塞→`0.1.11` 真包→`0.1.12` 即时重启）。
- **原生图标**：`assets/icon.png`（`hairyf/deepseek-harness-desktop` 同款 `512`）随包，`deb/rpm` 安装到 `hicolor` 并在 `.desktop` 设 `Icon=deepseek-harness-desktop`，`ryn.json:identifier` 与 `StartupWMClass` 均为 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`，`Wayland`/`X11` 任务栏均正确关联。
- **可测试宿主层**：`HarnessRuntimeHost` / `RuntimeSupervisor` / `RuntimeLocator` / URL 解析器均带 xunit 单测（`12/12`）与门禁；`package-linux.sh` 在 `staging` 即 `fail loud` 校验真包。

## 快速开始（开发）

```sh
# 前置：.NET 10 SDK；Linux 需 WebKitGTK
# 可选：从本机 dsh 安装预置捆绑运行时（需 node + @deepseek-ai/dsh）
scripts/bundle-runtime.sh

# 运行——默认用 PATH dsh；用内置运行时加：
# DSH_DESKTOP_RUNTIME_DIR=$PWD/resources/runtime
dotnet run --project src/DeepSeek.Harness.Desktop

# 测试
dotnet test dotnet-deepseek-harness-desktop.slnx

# WebView 调试器（默认关）
DSH_DEVTOOLS=1 dotnet run --project src/DeepSeek.Harness.Desktop
```

> 运行时需要可达的 DeepSeek provider 凭据（如 `DEEPSEEK_API_KEY`）才能提供 Web UI。

## 目录

```
├── src/DeepSeek.Harness.Desktop/   # Ryn 壳：Program（后台市场+重启）、Services/HarnessRuntimeHost、RuntimeSupervisor、RuntimeLocator、HarnessUrlParser、Commands
├── tests/DeepSeek.Harness.Desktop.Tests/  # xunit 12/12
├── resources/runtime/              # 内置 Node + dsh 闭包 + dshmarket.tgz 497K（生成物，gitignore，staging 校验真包）
├── scripts/                        # 门禁 + bundle-runtime{-ci,}.sh + package-linux.sh
├── .agents/notes/implemented/bug-fix/2026-08-20-dshmarket-background-install.md  # 市场链路 ADR
└── docs/                           # 项目文档（待建）
```

## 许可与致谢

- 本项目：MIT。
- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)（MIT）—— 本壳托管的运行时。
- [Ryn](https://github.com/Yupmoh/Ryn)（MIT）—— 桌面壳框架。
- 打包思路参考 [pilot-harness](https://github.com/op7418/pilot-harness)。
