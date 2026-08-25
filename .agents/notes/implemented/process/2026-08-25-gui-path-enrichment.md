# Agent Note: gui-path-enrichment

Status: implemented

## Problem

Linux GUI 会话应用继承的 PATH 是 systemd 用户管理器的默认精简值——不含 `~/.local/bin` 等用户级 bin 目录（2026-08-25 本机实证：`systemctl --user show-environment` 无自定义 PATH，各登录配置文件零注入）。桌面壳 spawn 的 dsh 运行时及其 MCP stdio 子进程因此解析不到用户级命令。叠加实验实证：mcp-client 对拉起失败**端到端零诊断**——坏实例（`command: /nonexistent/path/npx`）在真实捆绑运行时下 30 秒输出仅 `dsh web:` 一行，`~/.dsh` 全盘无失败痕迹，重连放弃亦无日志。

## Decision

`HarnessRuntimeHost.StartCoreAsync` 在 spawn dsh 前把 `$HOME/.local/bin` **追加**进子进程 PATH：`BuildEnrichedPath` 纯函数（缺则追加、已含幂等、分隔符随平台、空 PATH 兜底）。追加而非前置——不改变系统命令解析优先级。仅运行时 spawn 这一处注入；插件安装链（`plugin add`）不需要用户命令，保持最小面。

配套：docs/faq 双语新增「自己接了 MCP 服务器但工具不出现」条目，钉两条纪律——stdio 的 `command` 写绝对路径；工具不出现即连接失败（当前无提示）。

## Alternatives considered

- **不动（维持父环境原样透传）**：落败——GUI 场景裸命令名必败且零诊断，用户无从排查；一处纯函数即可消除最常见的 ENOENT 成因。
- **注入完整终端 PATH / source 用户 profile**：落败——把不可控的 shell 配置面拖进产品进程树，行为不可预测且难测试。
- **只推上游修 mcp-client 可见性**：正交而非替代——上游完全静默已实证，可见性修复经官方 Discussions 通道另行推动（CONTRIBUTING 指定 bug 报告走 Discussions）；PATH 补全不依赖其节奏。
- **要求用户自配 `~/.config/environment.d/`**：落败——要求每个用户改登录环境才能用上功能，违背「顺手」准则。

## Consequences

- 收益：stdio MCP 裸命令名（`~/.local/bin` 内的 node/npx 等）在 GUI 启动场景可解析；对既有绝对路径接线零影响（幂等）；系统命令解析优先级不变。
- 代价/风险：子进程 PATH 与系统默认存在一处加法差异（记录于本笔记与代码注释）；Windows 上分隔符随 `Path.PathSeparator` 自适应，该目录在 Windows 罕见、追加无害。
- 验证：`dotnet test` 全绿 0 警告，新增 `BuildEnrichedPath` 边界用例（null/空 PATH 兜底、缺失追加、已含幂等）。

## Related

- `implemented/feature/2026-08-25-bundled-plugin-version-aware-catalog`：同日落地；本决策源于其为 MCP stdio 接线排查环境时的发现。
