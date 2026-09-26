# Agent Note: webkit-sandbox-userns-fallback（WebKit 沙箱 userns 受限降级）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 兑现 [`smoke-linux-xvfb-fullchain`](../testing/2026-09-26-smoke-linux-xvfb-fullchain.md) 的真验证待办；dispatch 实证见本 ADR Testing。

## Problem

`workflow_dispatch` 跑 package-linux（run `36227897598`，本批 ADR 合入后）：Xvfb 生效（窗口创建、两腿均命中① `dsh web =`），但两腿 FAIL——WebKitGTK 的 bwrap 渲染沙箱在 hosted runner 的无特权 userns 下起不来即整进程 core（amd64：`bwrap: loopback: Failed RTM_NEWADDR: Operation not permitted`；arm64：`bwrap: setting up uid map: Permission denied`；随后 `timeout: the monitored command dumped core`），导航 0 到达，落定 FAIL（fail loud 符合设计，不是误报）。同跑实证通过三项：P1 npm-bin 暴露（`npm 全局 bin 已暴露` + `VerifyDsh` 过 `dsh 0.1.7-alpha.2`）、P2 无人值守跳过行、截图 artifact 开火。

真机分层（证据分级）：Fedora/Debian/Arch 系不会——userns 默认开，且本机多次实机验收导航正常即反证；Ubuntu 24.04+ 真机有可能——AppArmor 默认限制非特权 userns，Electron 系在该平台已有 `--no-sandbox` 先例，WebKitGTK 是否在例外名单无证据；容器/加固内核会。CI 与部分真机同病，须产品侧条件降级 + CI 逃生舱双轨。

## Decision

- CI 冒烟步骤 env 加 `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1`（测试环境逃生舱；产品不受此项影响）。
- Core 新纯策略 `WebkitSandboxPolicy`（对标 `WebAuthRecovery`）：`Evaluate(isLinux, usernsCloneContent, apparmorRestrictContent)`——非 Linux 一律 Keep；`unprivileged_userns_clone == "0"` 或 `apparmor_restrict_unprivileged_userns == "1"` 任一命中即 Disable；缺失/不可解析一律 Keep（secure default：只在阳性信号下关沙箱）。
- 组合根新分部 `DesktopBootstrap.WebkitSandbox.cs` 仅编排（R1）：`Run()` 入口先于一切 WebView 创建读两 sysctl 文件（读失败按缺失计）→ 命中 Disable 则 `Environment.SetEnvironmentVariable`（不同值覆盖为 `1`，同值幂等，从不 unset）+ loud 日志 `[host] WebKit 沙箱已禁用（userns 受限），renderer 无隔离`；用户预设仅在 Keep 路径下原样保留。
- 降级可观测：禁用与否的判定输入落在 loud 日志行，可从 CI/实机日志直接判读。

## Alternatives considered

- **产品全量常关沙箱**：落败——对全部用户静默降 renderer 隔离，安全代价无差别摊派；阳性才关是底线。
- **只写 FAQ 不做产品降级**：落败——Ubuntu 受限用户拿到的是 crash loop + bwrap 黑话日志，文档救不了首启链路。
- **容器/CI 启发式探测（如 /.dockerenv、CI env）**：落败——脆弱指纹，真机 Ubuntu 反而漏网；sysctl 阳性信号是直接证据。
- **什么都不做（退回恒②）**：落败——等于宣告 Linux 全链永不验证 + 放任 Ubuntu 真机崩溃，Xvfb 批白做。

## Consequences

- CI runner 与 userns 受限桌面首启可活（renderer 无隔离，日志明示）；正常机器零变化（secure default）。
- 代价：受限主机上 renderer 进程无 bwrap 隔离——显式、记日志、可归因（强于静默 core）。
- Ubuntu 24.04 真机证据仍缺（跟进：待社区/实机取 `apparmor_restrict_unprivileged_userns` 值与禁用后导航证据）。

## Testing

- `WebkitSandboxPolicyTests` 矩阵：非 Linux 全 Keep；userns `0/1`/缺失/脏串；apparmor `1/0`/缺失；双阳性。
- 接线薄层沿既有先例不单测；真验证 = 重 dispatch package-linux（预期 full-chain 或仅真实问题 fail loud）。
