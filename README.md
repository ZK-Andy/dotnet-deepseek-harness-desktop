# DeepSeek Harness Desktop for .NET

中文 | [English](README.en.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![build & test](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml/badge.svg)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml)
[![tests](https://img.shields.io/badge/tests-25%2F25-brightgreen)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml)
[![coverage](https://img.shields.io/badge/coverage-26.4%25-orange)](docs/testing.md)
[![platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-4f6ef7)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases)
[![.NET](https://img.shields.io/badge/.NET-net10.0-512bd4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![docs](https://img.shields.io/badge/docs-architecture%2Ftesting%2Fdevelopment-blue)](docs/architecture.md)

**[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（MIT）的 .NET 桌面客户端**，基于 [Ryn](https://github.com/Yupmoh/Ryn)（Tauri-for-C# 原生 WebView 框架）。桌面壳**内置完整 DeepSeek Harness 运行时**——终端用户**无需单独安装 Node / DeepSeek Harness**。

> 现状：`v0.1.12`（`25/25` 单测 `26.4%` 覆盖 + 门禁全绿，`CI` 双绿）——原生壳 + 内置运行时 + 崩溃恢复 + 插件市场已闭环；`Wayland`/`图标` 已正，`0.1.11` 起市场随包 `497K` 真包 + 后台 `JSON` 检测/迁移/`allowBuilds` 自愈，`0.1.12` 后台装完自动重启即现。

## 亮点

- **原生轻量壳**：C# 后端 + HTML/CSS/JS 前端，跑在系统 WebView（WebView2 / WKWebView / WebKitGTK），NativeAOT 就绪，能力沙箱 deny-by-default（`ryn.json`）。
- **完整运行时内置**：`resources/runtime/` 内置 Node 二进制 + `@deepseek-ai/dsh` 依赖闭包（`pilot-harness` 整树 `node_modules` 模型，`--store-dir` 规避 `sqlite` 锁）；壳以私有 `DSH_HOME` 拉起 `dsh web`，UI 加载 `dsh web:` URL。无 PATH `dsh`/`node` 也能跑（缺内置时回退 PATH dsh）。
- **崩溃恢复**：壳监督运行时子进程——退出即显示恢复屏、重启子进程、把同一窗口导航到新 URL；**端口保持稳定**，Web UI 的 origin（及页面级会话记忆）在重启后存活——**崩溃后回到之前对话**。
- **插件市场预装**：`dsh-market`（`https://github.com/dsh-market/dsh-market`）已随包 `497K` 真包到 `resources/runtime/dshmarket.tgz`——首启后台 `file://` 静默安装到 `DSH_HOME`（`System.Text.Json` 精确检测、`app` 假依赖迁移、`pnpm-workspace.yaml 6` 项 `allowBuilds` 自愈），装完由 `RuntimeSupervisor` 重启即现，`1200+` 插件可搜一键装（`0.1.8` 阻塞→`0.1.11` 真包→`0.1.12` 即时重启）。
- **原生图标**：`assets/icon.png`（`hairyf/deepseek-harness-desktop` 同款 `512`）随包，`deb/rpm` 安装到 `hicolor` 并在 `.desktop` 设 `Icon=deepseek-harness-desktop`，`ryn.json:identifier` 与 `StartupWMClass` 均为 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`，`Wayland`/`X11` 任务栏均正确关联。
- **可测试宿主层**：`HarnessRuntimeHost` / `RuntimeSupervisor` / `RuntimeLocator` / `MarketInstallHelper` / URL 解析器均带 xunit 单测（`25/25`，`MarketInstallHelper 84%`）与门禁；`package-linux.sh` 在 `staging` 即 `fail loud` 校验真包。

## 平台包

参照 [Ryn](https://github.com/Yupmoh/Ryn) 的 `ryn bundle`（`macOS .app` / `Windows` 文件夹 + `WiX` / `Linux AppDir`）与 `Ryn` 的 `release.yml` 矩阵（`osx-arm64`/`linux-x64`/`win-x64` 各在原生 `OS` 上 `dotnet publish`），本项目 `PublishAot=false` 可交叉编，故 `macOS` 两档均用 `macos-latest` 单 `runner` 矩阵内切 `rid`（`ARM via Rosetta`），避免占 `2` 台 `mac`。

| 平台 | 架构 | 包格式 | `Runner` | 测试情况 | 客户端 |
|---|---|---|---|---|---|
| `Linux` | `x64` (`amd64`) | `deb`/`rpm` | `ubuntu-latest` | ✅ `CI` 自动（`staging` 真包 + `rpm -qp --requires` + `deb Depends`） | 🟡 `deb` / 🟢 `rpm` |
| `Linux` | `arm64` | `deb`/`rpm` | `ubuntu-24.04-arm` | ✅ `CI` 自动（矩阵 `arm64`） | 🟡 `deb` / 🟢 `rpm` |
| `macOS` | `arm64` (`osx-arm64`) | `zip` (`.app`) | `macos-latest` | ✅ `CI` 自动（单 `runner` 矩阵） | 🟡 |
| `macOS` | `x64` (`osx-x64`) | `zip` (`.app`) | `macos-latest` (交叉) | ✅ `CI` 自动（`Rosetta`） | 🟡 |
| `Windows` | `x64` | `zip` | `windows-latest` | ✅ `CI` 自动（`zip`/`powershell` 回退） | 🟡 |

`Linux` 为 `rpm` 锚可本地 `ARCH=arm64 bash scripts/package-linux.sh --stage-only` 验证；`mac/win` 已切 `tag+workflow_dispatch` 手动触发，`CI` 全量时同出 `SHA256SUMS`。

> 🟢 已针对性测试（`rpm` 可本地 `rpm -qp --requires` 验证），🟡 已实现但未针对性测试（仅 `CI` 自动）。

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
├── src/DeepSeek.Harness.Desktop/   # Ryn 壳：Program（后台市场+重启）、Services/HarnessRuntimeHost、RuntimeSupervisor、RuntimeLocator、MarketInstallHelper、HarnessUrlParser
├── tests/DeepSeek.Harness.Desktop.Tests/  # xunit 25/25
├── resources/runtime/              # 内置 Node + dsh 闭包 + dshmarket.tgz 497K（生成物，gitignore，staging 校验真包）
├── scripts/                        # 门禁 + bundle-runtime-ci.sh（bundle-runtime.sh 为透传）+ package-linux.sh
├── .agents/notes/implemented/bug-fix/2026-08-20-dshmarket-background-install.md  # 市场链路 ADR
└── docs/architecture.md、testing.md、development.md  # 项目文档
```

## 许可与致谢

- 本项目：MIT。
- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)（MIT）—— 本壳托管的运行时。
- [Ryn](https://github.com/Yupmoh/Ryn)（MIT）—— 桌面壳框架。
- 打包思路参考 [pilot-harness](https://github.com/op7418/pilot-harness)。
