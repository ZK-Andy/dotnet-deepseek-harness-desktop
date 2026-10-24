# Agent Note: mac 崩溃取证面修复——typo/落盘轮询/异名兜底

Status: implemented

## Problem

打包冒烟 dispatch run 40116948207（mac x64 腿）以 SIGILL 收红：应用进程被 Illegal instruction 杀死（shell 信号行实证，Rosetta 转译层），但崩溃取证零产出——smoke-log-macos-x64 仅 1402 字节纯日志、无 .ips。诊断出取证面三缺陷：

1. `smoke_collect_crash_reports` 内 `cp "$f" "$SMO_LOG_DIR/"` 变量名少 M（应为 `SMOKE_LOG_DIR`），`set -e` 下被 `|| true` 静默吞——即使 find 命中 .ips 也拷不进 artifact。
2. 取证查询跑在 ReportCrash 落盘前：进程死后约 1s 即出「无 .ips」结论，单次快照先天错过异步写出的崩溃报告。
3. 按进程名精确匹配对 Rosetta 转译崩溃的报告命名不确定；且「进程非崩溃退出或未落报告」措辞与 SIGILL 事实矛盾，误导后续诊断方向。

## Decision

取证面拆为共享库 `scripts/lib/smoke-crash-lib.sh`（smoke-install-macos.sh 超 250 行尺寸闸，先拆再上）：

- 主流程红腿时收割应用退出码：`kill -0` 判死（bash 对后台子进程即时收割记账、不留僵尸——`ps stat=Z` 判不死，评审 R2 B1 实证修正）后 `wait` 从记账即时取回 128+N，进程仍活不阻塞；`smoke_reap_app_exit` 写全局 `APP_EXIT` 传入取证面（不经命令替换——wait 只能等本 shell 的子进程）。
- 退出码 ≥128（信号死）时有界轮询等 .ips 落盘：`SMOKE_CRASH_WAIT_SECONDS` 默认 15s、2s 间隔；非信号死单次快照不等待。
- 按进程名轮询无果后放宽收窗内全部 `*.ips`（30 分钟窗；runner 每作业全新 VM，无跨跑污染）。
- 无报告措辞按退出码如实：信号死报信号号与等待时长，不再断言「进程非崩溃退出」。
- 诊断目录参数化（`SMOKE_CRASH_REPORT_DIRS` 数组，同进程赋值）入自测；7 条自测夹具：收割门两条（活进程返 0 不阻塞 / 死进程取回退出码）、typo 回归钉（.ips 必真拷进 `SMOKE_LOG_DIR`）、摘要解析、信号死轮拾异步落盘报告、无报告如实措辞、异名兜底。

## Alternatives considered

- **只修 cp typo 不加轮询/放宽**：40116948207 实证 SIGILL 后 1s 查询零 .ips——落盘延迟是零证据的另一半根因，单修 typo 下次红腿仍大概率无证据。弃。
- **`log show`/统一日志查崩溃**：CLI runner 权限与输出形态不稳，.ips 是 macOS 标准崩溃证据面。弃。
- **非信号死路径也轮询**：绿腿每次白等 15s；轮询仅在退出码可证明信号死时有证据收益。弃。
- **`wait` 直接收割不判僵尸**：红腿时进程可能仍活（见证红形态），wait 会阻塞挂死冒烟收尾。弃。

## Consequences

- 下次 mac x64 信号死红腿应带出 .ips，或如实报告「等待 Ns 未见落盘」——区分「没有证据」与「确认无报告」两种结论。
- SIGILL 根因本身（Rosetta 转译层，应用侧）仍未修——本 ADR 只修取证面，根因定位依赖下次红腿的 .ips 产出。
- 自测 +≈7s（三条轮询夹具各烧满 2s 预算 + 收割门两条）；`smoke-crash-lib.sh` / `smoke-install-macos.sh` 均在 250 行尺寸闸内。
