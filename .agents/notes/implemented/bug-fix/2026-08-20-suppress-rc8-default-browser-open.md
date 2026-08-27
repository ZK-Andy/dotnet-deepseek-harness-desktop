# Agent Note: suppress-rc8-default-browser-open

Status: implemented

## Problem

升级 `@deepseek-ai/dsh` 到 `0.1.0-rc.8`（见 `.agents/notes/archived/process/2026-08-20-upgrade-bundled-dsh-rc8.md`）后，桌面壳出现双重开窗：壳按既有方式 spawn `dsh --profile web --port 0`、解析 `dsh web:` URL 渲染进内嵌 Ryn WebView；但 rc.8 的 `dsh-web-app` 新增 `openBrowser` 配置且**默认 `true`**（`lib/index.js` `openBrowser: z.boolean().default(true)`，`handoffBrowser = config.openBrowser && !launchedThroughSsh(ctx)`），服务就绪时会把同一 URL 交给 OS 默认浏览器（`openBrowser(webUrl)`，提示 `pass --no-open to disable`）。结果用户同时看到 dsh 自动弹出的浏览器页面 + 桌面窗口。

## Decision

**桌面壳 spawn dsh web 时显式追加 `--no-open`**：

- `HarnessRuntimeHost.StartCoreAsync` 的参数构造收敛为 internal 辅助方法 `BuildDshWebArgs(int? port)`，返回 `["--profile","web","--port",port?:"0","--no-open"]`；壳据此自渲染，不再外开系统浏览器。
- 为使该辅助方法可单测，主工程 `DeepSeek.Harness.Desktop.csproj` 增加 `<InternalsVisibleTo>` 指向测试程序集；`HarnessRuntimeHostTests` 新增回归断言参数含 `--no-open`。
- 行为不变的部分：端口复用（origin 稳定）、崩溃重启、`dsh web:` URL 解析、捆绑运行时定位——均不受影响。

## Alternatives considered

- **不改代码、发布前仅在 README 提示用户手动关**：落败——双击体验应为免调，且 `--no-open` 是 rc.8 服务的正解；把运行期开关硬性依赖用户操作不可接受。
- **在 dsh 侧通过补丁（`--patch`）把 `openBrowser` 关掉**：落败——`--no-open` 是面向用户的服务级开关，语义直白、随运行时稳定，无需维护额外 patch 文件。
- **只在外层杀掉弹起的浏览器进程**：落败——竞态、平台差异大，是治标。

## Consequences

- 收益：桌面端运行 dsh rc.8 时不再额外弹出 OS 浏览器；行为回到 rc.8 之前"仅内嵌 WebView"的预期；参数单点可测。
- 代价/风险：万一未来 dsh 移除 `--no-open` 会导致参数被忽略（行为退化）；`--no-open` 同时会抑制 `printUrl` 之外的"opening default browser"日志，无碍本壳（壳自打印 `dsh web:` URL）。
- 验证：`dotnet test 26/26` 全绿（新增含 `--no-open` 断言）；真实桌面未在本会话复跑（沙箱渲染受限），依赖下次真机启动确认无浏览器弹出。
