# Agent Note: smoke-runner-deepening（hosted runner 验证深挖三件）

Status: implemented

Review: FULL/2026-09-25/R1=ok R2=ok R3=ok

## Problem

三平台冒烟在 hosted runner 上的实际覆盖长期靠"翻日志才知道"：`smoke-install-*.sh` 双信号（①全链/②安装链）命中哪个不打印结论；mac x64 包在 ARM runner（Rosetta）下是否真跑过无记录；Windows 运行态停在安装链（Ryn Run 即退出，2026-08-29 实证记为 WebView2 原生条件缺失）后无下文；"页面长什么样"零证据。wdio 式真窗口 E2E 在本仓不做（理由见 Alternatives FlaUI 条），但 hosted runner 的验证深度明显没挖透——有桌面会话（mac WindowServer、Windows 交互会话）的机器在每次发版都空转着。

## Decision

三件，皆小，全部落在冒烟脚本 + 调用 workflow，不动产品代码：

**1｜verdict 明确化**：三脚本在 PASS 点（含"进程已死但补扫命中"的早退出口）按命中信号打印 `SMOKE_VERDICT=full-chain|install-chain`（`[host] dsh web =` 命中即全链，否则安装链）。mac 行追加 `arch=$(uname -m)`——x64 leg 跑在 `macos-latest`（ARM64）上即 Rosetta 下验证，结论自带出处，不再靠人记矩阵（artifact 名另带 matrix.arch 防互顶）。

**2｜启动截图 best-effort**：三脚本在 PASS（含早退出口）/失败取证后尝试截屏（mac `screencapture -x`；win PowerShell `System.Drawing` 全屏；linux deb 路径仅 `$DISPLAY` 非空且有 `import/scrot/gnome-screenshot` 才试，rpm 容器路径除外——无 DISPLAY、无目录注入，恒跳过），一律 `|| true` 永不拦冒烟；落盘目录由调用方经 `SMOKE_SHOT_DIR` 注入（未设即跳过，本地跑零打扰）。三 workflow 各加 `upload-artifact if: always()`（`if-no-files-found: warn`，7 天）供人眼复核。

**3｜Windows Evergreen 实验（硬步骤）**：`package-windows.yml` 在冒烟前加步——下载 Evergreen standalone（`go.microsoft.com/fwlink/p/?LinkId=2124703`，`curl --fail` + 重试 3 次）、`/silent /install`（`MSYS2_ARG_CONV_EXCL='*'` 防 Git Bash 路径转换吞开关，同脚本 `/VERYSILENT` 先例）、`EdgeWebView\Application` 版本目录枚举留痕（证据位，fail loud 由安装器退出码承担）。失败即红（fail loud）：runner 会话缺的是 WebView2 初始化条件还是别的，红或绿都是 verdict；MS CDN 抖动按普通网络 flake 重跑（与引导下载 node 同等待遇）。若此后 ① 命中，Windows 运行态即进 CI；仍不命中，退出码 + 截图 + 日志就是下一轮的证据。

## Alternatives considered

- **Linux Xvfb 全链**：落败（本批不做）——GTK 在 Xvfb 下大概率能起，一旦起得来等于解锁 Linux 运行态，收益大；但它是第 4 件，会撑大本批 diff 与验证面，记下批（待办）。
- **Evergreen 步骤 `continue-on-error`**：落败——实验红了若静默继续，本轮零结论还浪费一次发版验证窗；抖动重跑即可，与既有网络依赖同哲学。
- **截图失败即拦冒烟**：落败——截图是人眼证据位，不是正确性判据；无显示/无工具时跳过并留痕一行。
- **FlaUI/Playwright 真窗口断言**：落败——wdio 式真窗口 E2E 在本仓投入产出比为负：单维护者 + 发版当天真机验证已闭环，瓶颈是机器覆盖而非验证速度；Ryn 无内嵌 WebDriver，造 harness 是重资产；常红的 flaky 车道反而拖慢发版（否决理由自含本条，不另立出处）。本批只做到"看得见"（截图 artifact），不断言。
- **mac 自签/Gatekeeper 真机**：不相关——同 runner 挂载不经 quarantine（既有注释），真机首启仍是签名挂账，不在本批。

## Consequences

- 每次发版的三平台 job 日志各多一行 verdict + 可选一张截图 artifact；Linux 无显示时截图步静默跳过。
- Windows 若 ① 命中：运行态验证自动生效，后续回归（导航/恢复/收养类）多一台机器；仍不命中：本轮的红/证据即下一轮输入，不亏。
- 新增外部网络依赖一处（MS Evergreen 链接）：抖动按 flake 重跑；链接失效（LinkId 变更）按"任一红先修再发"修。
- `SMOKE_SHOT_DIR` 未设时三脚本行为与改前一致（截图/上传全跳过），本地跑零影响。

## Testing

- `bash -n` 三脚本；`verify-*` 门禁全绿；`verify-review-tier` 定档（workflow 在 FULL 表）。
- 真验证在 CI：三平台 package job 全绿 + 日志见三行 verdict + artifact 有截图（Linux 预期 warn 空过）；Windows Evergreen 步的版本号留痕无论红绿都是结论。
