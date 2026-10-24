# Agent Note: 打包冒烟三红腿修复——win 清单、rpm 容器 Xvfb、mac 崩溃取证

Status: implemented

Review: FULL/2026-10-25/R1=ok R2=ok R3=ok

## Problem

package 手动预览（run `40109768884`，main@`bf16a20`）四腿红、全部红在安装冒烟步——Avalonia 迁移（G0–G5）后首次真启动打包产物（ci.yml 不启应用），三处独立根因：

1. **Windows**：包无 `app.manifest`（supportedOS 声明）→ Avalonia Win32 `NativeControlHost` 建 WebView 子窗抛 `InvalidOperationException: Unable to create child window for native control host. Application manifest with supported OS list might be required.`（栈 `NativeWebView.OnAttached`）→ 未处理异常即崩。旧 Ryn 宿主自管原生托管，该前提面不存在。
2. **Linux rpm 容器腿**（两 linux 作业各一）：`fedora:44` 容器无 DISPLAY，Avalonia 启动即初始化 X11、`XOpenDisplay failed` 硬崩；旧宿主无显示也能出②信号（GUI 后置），工装 header 注明的「无 X 照跑」前提失效。同作业 deb 腿（CI 层 `xvfb-run`）全链绿，证明应用本体在 X11 无恙。修复 = inner **单独一条** `dnf install xorg-x11-server-Xvfb libatomic`（与主体应用安装分开、失败显式留痕）。**后续递进（run `40113917704`）**：Xvfb 修复在 amd64/arm64 双腿实证（Xvfb 装上、应用 X11 全链起）后，arm64 容器红在下一层——bootstrap 下载的 Node 官方 **arm64** 包动态链 `libatomic.so.1`，fedora 最小镜像没有（npm install exit=127）；x64 包不依赖故 amd64 腿无恙。`libatomic` 即随该递进并进同一条安装行。
3. **mac x64（Rosetta）**：启动 ~14s `Illegal instruction: 4`，无任何窗口出现（fail 截图实证桌面无窗），时机在 update Checking 之后、WebView 桥日志之前；同包 mac arm64 全链绿。冒烟工装只收 stdout，**无崩溃报告取证面**——根因不可判，已标【推断 · 未证】WKWebView 原生垫层 ABI（与 Ryn 时代 Rosetta segfault 同域、不同栈）。

## Decision

1. **Windows**：主工程加 `app.manifest`（supportedOS Win7/8/8.1/10/11 四 GUID + asInvoker）+ csproj `<ApplicationManifest>`；非 Windows 构建静默忽略（清单仅 Windows PE 消费）。
2. **rpm 容器腿**：inner 在应用安装成功后**单独** `dnf install xorg-x11-server-Xvfb libatomic`（装失败显式 error 留痕、不掩埋应用安装结果；`libatomic` 为 bootstrap 下载的 Node 官方 arm64 包运行时依赖，fedora 最小镜像没有，x64 包不依赖——递进发现见 Problem 条 2）；启动改为「无 DISPLAY 且有 xvfb-run 时包 `xvfb-run -a`」——容器腿从「无显示②保底」升级为 Xvfb 下全链①判定。两脚 header 同步现行态。
3. **mac 崩溃取证**：`smoke-install-macos.sh` 新增 `smoke_collect_crash_reports`——收 `DiagnosticReports` 内 30 分钟窗口的本应用 `.ips`（用户与系统两目录）全量拷入 `SMOKE_LOG_DIR` 随 artifact 上传，python3 提取异常类型/终止原因/故障线程前 15 帧（映像名）打进步日志；提取失败只留痕不连累冒烟结论（与截图同 best-effort 纪律）。

## Alternatives considered

- **应用侧容忍无显示启动（延迟 X11 初始化）**：落败——与框架启动模型对抗且违反 fail loud 行为契约；deb 腿证明 X11 下应用无恙，该修工装不修应用。
- **mac x64 直接猜根因修**（如换 RID/闸门禁用 webview 原生垫层）：落败——SIGILL 无崩溃报告佐证，任何「修法」都是未验证主张上线；先取证，下一轮红的 .ips 给确定性根因再修。
- **Xvfb 与应用合并一条 dnf 命令**：落败——Xvfb 包若在 fedora:44 缺失（X server 系包退役风声，本机无 docker 未预验）会把「应用装好但缺显示」混成「应用装不上」，分两条装保住诊断面。
- **rpm 容器腿维持无显示、只等②保底**：落败——维持现状即维持必红；且 Xvfb 修好后①可达，容器腿覆盖面顺势升级为全链。

## Consequences

- 待证项处置：`xorg-x11-server-Xvfb` 在 `fedora:44` 的可用性已由 run `40113917704` 双腿实证（装上并全链跑通）；后续待证项只剩 mac x64 SIGILL 根因与 arm64 的 libatomic 实证（修复随 2026-10-25 第三批推送）。
- mac x64 本批**只补取证不修根因**——若下一轮 .ips 指向包内原生垫层，修法面（上游 issue / 降级闸 / RID 取舍）另开决策。run `40113917704` 的 mac x64 job 为 hosted runner 基建失联（2m15s 即死、零步骤级错误、步骤结论面自相矛盾），非应用证据；SIGILL 根因仍待基建正常的 run 取 .ips。
- Windows DPI 感知未顺手加进清单（PerMonitorV2 属观感行为变更，超出本批「冒烟转绿」边界）；若真机观感需要再走独立决策。Windows 腿已由 run `40113917704` 实证转绿（manifest 修复生效）。
- 打包冒烟的「作业红但含绿冒烟本体」（deb 全链绿、作业因 rpm 容器红）语义已在 HANDOFF 记账，勿重复归因。

## Testing

- 三脚本 `--self-test` 全过（rpm inner 端到端夹具 55 ok 含 mac 自测）；`dotnet build` 0 警告 0 错误。
- 真实验证面 = 下一次 package dispatch 五腿（不在本批内跑，等用户确认）。
