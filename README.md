<p align="center">
  <img src="assets/icon.png" width="96" alt="DeepSeek Harness Desktop for .NET">
</p>

# DeepSeek Harness Desktop for .NET

<p align="center"><a href="README.en.md">English</a> · <strong>中文</strong></p>

<p align="center"><strong>DeepSeek Harness（MIT）的 .NET 桌面客户端——简单壳，依赖全机唯一的一份全局 dsh：无则装、落后则更新到 @alpha。</strong></p>

<p align="center">
  <a href="#功能">功能</a> ·
  <a href="#下载安装">下载安装</a> ·
  <a href="docs/user-guide.md">用户指南</a> ·
  <a href="docs/faq.md">常见问题</a> ·
  <a href="#工作原理">工作原理</a> ·
  <a href="#开发">开发</a> ·
  <a href="docs/architecture.md">架构</a> ·
  <a href="LICENSE">MIT License</a>
</p>

<p align="center">
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases"><img src="https://img.shields.io/github/v/release/ZK-Andy/dotnet-deepseek-harness-desktop?style=flat&label=release&color=4D6BFE" alt="release"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases"><img src="https://img.shields.io/github/downloads/ZK-Andy/dotnet-deepseek-harness-desktop/total?style=flat&label=downloads&color=4D6BFE" alt="downloads"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop"><img src="https://img.shields.io/github/stars/ZK-Andy/dotnet-deepseek-harness-desktop?style=flat&label=stars&color=4D6BFE" alt="stars"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml"><img src="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml/badge.svg" alt="build &amp; test"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml"><img src="https://img.shields.io/badge/tests-457%2F457-brightgreen" alt="tests"></a>
  <a href="docs/testing.md"><img src="https://img.shields.io/badge/coverage-52.2%25-yellowgreen" alt="coverage"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases"><img src="https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-4f6ef7" alt="platform"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img src="https://img.shields.io/badge/.NET-net10.0-512bd4" alt=".NET"></a>
</p>

**[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（MIT）的 .NET 桌面客户端**，基于 [Ryn](https://github.com/Yupmoh/Ryn)（Tauri-for-C# 原生 WebView 框架）。桌面壳是**简单壳，依赖系统全局 node + 全局 dsh**（都在用户 PATH 上，`npm install -g @deepseek-ai/dsh@alpha` 装/更新，桌面与终端共用同一套、无版本分叉）。首次启动检测全局 dsh：没有 → 装；落后 `@alpha` → 更新到 `@alpha`；已最新 → 直接用。**node 走系统全局**（用户那份 node/npm，或没有则桌面下载最新官方 node 装到系统全局位供共用），桌面不自备私有 node / 私有 PATH。

## 功能

- ⚡️ **简单壳、依赖系统全局 node + 全局 dsh（online-first）** — 安装器只带壳（约 30MB 级），不运输行时闭包。首启检测用户 PATH 上的全局 dsh：没有 → 经全局 node 的 `npm install -g @deepseek-ai/dsh@alpha` 装到系统全局位；落后 `@alpha` → 更新到 `@alpha`；已最新 → 直接用。**dsh 跟随 alpha 预发布通道，不钉版**。node 检查/下载只在"需要确保 dsh"时做一次（复用系统全局 node；无则桌面下载最新官方 node 装到系统全局位，写系统位需 sudo 时给出手动安装命令），不做重复联网。壳在共享数据目录 `~/.dsh` 以专属 `desktop` profile 拉起 dsh。会话/凭据/插件与 CLI、TUI、Web **同一宇宙互通**。
- ⌨️ **终端命令（CLI shim 注册）** — dsh 已全局在 PATH；壳把内容恒定的 `pnpm` shim 注册到用户 `PATH`（Windows `%LOCALAPPDATA%\deepseek-harness\bin` + `HKCU\Environment\Path` 幂等合并；mac/linux `~/.local/bin` + shell rc 幂等块；dsh 无需 shim），终端可直接用 `pnpm`。不覆盖用户自己的同名命令。

- 🔒 **原生轻量壳** — C# 后端跑在系统 WebView（WebView2 / WKWebView / WebKitGTK），NativeAOT 就绪，能力沙箱 deny-by-default（`ryn.json`）。
- 🔄 **崩溃自愈与会话回归** — 壳监督运行时子进程：崩溃 → 恢复页（原因/stderr 尾部 + 导出诊断 + 退出应用）→ 自动重启 → 同一窗口回到新 URL；**端口保持稳定**，Web UI origin（及页面级会话记忆）存活——**崩溃或重启后回到之前的对话**。
- 💓 **页面健康监护（假活看门狗）** — 壳侧只读探针轮询「dsh 进程在跑但页面空白」的假活形态（不注入脚本、不依赖桌面伴生插件存活）；连续空白达阈值即在预算内触发一次有界 reload 自愈，耗尽转观测、成功恢复复位预算——防误报引发无限重载循环。
- ⬆️ **自更新** — 启动后台检查一次 + 设置页手动检查；发现新版本一键安装并自动重启（安装包 `SHA256` 强校验；`macOS` 引导手动更新），详见下方[「自动更新」](#自动更新)。
- 🧩 **插件市场 + 版本感知升级 + registry 自管** — `dsh-market` 经首启引导页「插件准备」步（推荐 chip + 确认/跳过 + 日志回流）以 registry 安装到桌面专属 profile（`~/.dsh/profiles/desktop`），确认后与 dsh 内核一次就位（联网；跳过可稍后在应用内市场补装）；随包桌面伴生插件按版本感知升级——内置版本更新即自动升级、绝不降级；市场为 registry 形态（与用户自装完全等价：上游发新版市场内即提示，无需等桌面发版）；`1200+` 插件可搜一键装。
- 🖥️ **系统托盘** — 关闭窗口默认最小化到托盘（可在设置改为直接退出），托盘菜单提供显示主窗 / 检查更新 / 退出（随 dsh 语言中英切换）；退出经有序编排，运行时子进程干净回收。
- 🚀 **单实例仲裁** — 应用运行中再次点击启动器/图标，唤起既有主窗而非新开重复实例。
- 🔁 **开机自启** — 设置页一键开关（Linux XDG autostart / Windows 注册表 Run / macOS LaunchAgents）。
- 🖼️ **原生图标 / Wayland 就绪** — 图标随包入 `hicolor`，`ryn.json:identifier` 与 `StartupWMClass` 均为 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`，任务栏正确关联。

## 下载安装

从 [Releases](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases) 下载对应平台安装包（附 `SHA256SUMS.txt` 校验）：

| 平台 | 架构 | 包格式 |
|---|---|---|
| `Linux` | `x64` / `arm64` | `deb`（`…_linux-amd64.deb` / `…_linux-arm64.deb`）/ `rpm`（`…_linux-x86_64.rpm` / `…_linux-aarch64.rpm`） |
| `macOS` | `arm64` / `x64` | `dmg` |
| `Windows` | `x64` | `exe` 安装器（`…-setup.exe`） |

> **预览版提示**：项目处于早期预览阶段，可能存在破坏性变更（数据目录布局、配置格式、运行时组件版本等），**不保证跨版本数据兼容**；升级前请自行备份重要会话与配置。
>
> **未签名说明**：本项目**开源、不做付费签名**，发布包**未签名**。macOS 首次打开若见 Gatekeeper「来自身份不明的开发者」→ 右键「打开」或 系统设置 → 隐私与安全性 →「仍要打开」；Windows 若见 SmartScreen「未知发布者」→「更多信息」→「仍要运行」。开发/内部可用 `SELF_SIGN=1` 自签消除本机告警，用法见 [docs/development.md](docs/development.md)。
>
> **测试状态**：单元测试由 `CI` 自动跑（`dotnet test` 全绿，见上方徽章）；**真机验收至今只在 `Linux` `x64` `rpm` 包上做过**——`deb`、`Linux arm64`、`macOS`（`dmg`）、`Windows`（`setup.exe`）**均无真机验证**，仅经 CI 编译/打包/冒烟。`macOS x64` / `Windows` 真机针对性测试**等待社区支持**（本地无 mac Intel x64 / Windows 真机）。安装器、自更新、首启补装等依赖真机的链路请按 [docs/testing.md](docs/testing.md) 列出的边界对待，尝鲜前做好备份。

## 自动更新

- 启动时后台检查一次新版本（不轮询、不打扰）；也可在 **设置 → 桌面设置** 手动检查。
- 发现新版本后，侧栏底部出现下载按钮——点击即下载校验、**一键安装并自动重启**（`Linux` 会弹一次系统授权）。
- 安装包落地前经 `SHA256SUMS.txt` 强校验，校验失败拒绝安装。
- `macOS` 暂不支持应用内静默更新——检测到新版本时会提示手动下载 `dmg`。

## 工作原理

```text
┌──────────────────────────────────────────────────────┐
│ Ryn 壳（C#，系统 WebView）                            │
│   拉起 dsh → 解析 dsh web: URL → 加载 Web UI           │
│   崩溃监督 → 稳定端口重启 → 回到之前对话                 │
└─────────────────────────┬────────────────────────────┘
                          │ spawn + 共享 ~/.dsh · desktop profile
┌─────────────────────────▼────────────────────────────┐
│ 运行时（全局 dsh，简单壳）                               │
│   PATH 上的 @deepseek-ai/dsh@alpha（无则装/落后则更新） │
│   dsh web（localhost）                                │
└──────────────────────────────────────────────────────┘
```

（详细架构见 [docs/architecture.md](docs/architecture.md)。）

## 开发

```sh
# 前置：.NET 10 SDK；Linux 需 WebKitGTK
# 运行（开发环境必须带 DSH_DESKTOP_DEV=1——dev 隔离 home 与单实例后缀；无 PATH dsh 时走首启引导，需网络）
DSH_DESKTOP_DEV=1 dotnet run --project src/DeepSeek.Harness.Desktop

# 测试（457/457）
dotnet test dotnet-deepseek-harness-desktop.slnx

# WebView 调试器（默认关）
DSH_DEVTOOLS=1 dotnet run --project src/DeepSeek.Harness.Desktop
```

> 运行时需要可达的 DeepSeek provider 凭据（如 `DEEPSEEK_API_KEY`）才能提供 Web UI。

## 目录

```
├── src/DeepSeek.Harness.Desktop/   # Ryn 壳：Program + Services/（运行时监督、自更新、系统托盘、单实例仲裁、诊断导出等）
├── tests/DeepSeek.Harness.Desktop.Tests/  # xunit 457/457
├── scripts/                        # 门禁 + package-*.sh + release-preflight.sh + release-notes.sh
└── docs/architecture.md、testing.md、development.md
```

## 许可与致谢

- 本项目：MIT。
- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)（MIT）—— 本壳托管的运行时。
- [Ryn](https://github.com/Yupmoh/Ryn)（MIT）—— 桌面壳框架。
- 打包思路参考 [pilot-harness](https://github.com/op7418/pilot-harness)。
