# Agent Note: 孤儿残留「杀后仍活」误判——判活僵尸感知 + 杀后有界复验 + 启动等待节拍化

Status: implemented

Review: FULL/2026-10-25/R1=ok R2=ok R3=ok

## Problem

2026-10-25 真机三连暴露启动/恢复链的三个确定性缺口（`~/.dsh/logs/host.log` 实证）：

1. **僵尸态误判「杀后仍活」**：16:22 会话清扫孤儿 dsh（pid 795643）时 SIGKILL 已发出，但
   `RuntimeLineageProbes.TryIsAlive` 用 `Directory.Exists("/proc/<pid>")` 判活——僵尸进程在被收割前
   仍占 `/proc` 目录，被误报「复验命中但杀后仍活」，fail-loud 拒绝 spawn，干等 17s 直到孤儿自行消亡
   才由监督器恢复（16:22:41→16:23:00）。同根缺陷影响收养/残留判定全路径。
2. **spawn 段零日志**：`runtime = PATH dsh` 落在 spawn+等 URL 之后，9–41s 的等待窗口（全天 17 会话
   实测分布，41s 为 16:21 会话）中间无任何日志，无法归因卡在哪。
3. **holder 页零反馈**：dsh 冷启动 9–41s 高方差期间，holder 过渡屏只有静止 spinner——用户无从区分
   「慢」与「卡死」（实机截图 "Starting DeepSeek Harness..." 即此态）。

## Decision

1. **判活读 `/proc/<pid>/stat` 状态位**：`TryIsAlive` Linux 分支改为 `LinuxProcAlive`——stat 不存在或
   状态位 `Z`（僵尸）按死算；stat 读不到但目录在（权限异常）保守按活（fail loud 方向）。状态位提取
   `ExtractStateChar` 取最后一个 `)` 之后首字符（comm 可含括号/空格），纯函数可单测。僵尸不占端口
   不占资源，对收养/清扫/残留判定全部语境「死」语义一致。
2. **杀后有界复验**：`OrphanDshReaper.EnsureNoResidue` 杀后复查从单次即判改为 5 轮（首轮立即 +
   4×200ms 间隔，`delay` 委托注入，生产 `Thread.Sleep` / 测试 no-op）——SIGKILL 到消亡/僵尸化有毫秒级
   窗口，全轮仍活才 Unreapable。零误杀语义不变：只加确认轮次，不放行任何未死残留。
3. **spawn 节拍日志**：`StartCoreAsync` 起 spawn 前、拿到 pid 后、URL 就绪后各留一行（含预算秒数与
   耗时）——等待窗口可归因。
4. **holder 页启动耗时反馈**：新增 `bootElapsed` 行每秒更新（纯展示，不参与重试——重试链仍只由事件
   驱动，恪守「禁祈祷式加时」）；≥45s 追加「长于常规，明细在 host log」。首行 `HolderMarker` 不动
   （`WebAuthRecovery` 裁决依赖）。

## Alternatives considered

- **判活改端口探测**（杀后看 42859 是否仍监听）：语义更准（僵尸/活物都不占端口），但探活依赖具体
  端口参数，`TryIsAlive` 是无端口语境的通用边界 API（收养路径也用）；且端口空闲≠进程死（可能换了
  端口）。不取。
- **击杀升级**（TERM→宽限→KILL 进程组）：现击杀本就是 SIGKILL 整树（.NET Unix 语义），升级无意义；
  真正缺口在判活误报而非击杀力度。不取。
- **杀后轮询交给监督器下一轮**：把误判留到崩溃恢复循环里自愈（16:22 实测即此路径，耗 17s+）——
  把「可当场确认的事」推给全局恢复，代价是整段 holder 停摆。不取。
- **holder 页加定时重试**：违背 holder-mint-gate-deepening 的「禁祈祷式加时」决策（重试只由事件
  驱动）。只加展示层，不碰重试逻辑。

## Consequences

- 判活语义变化：僵尸 = 死。消费方（收养 `WaitAdoptedExitAsync` 轮询、残留预检、血统收割）一致受益；
  理论边界——若未来有「等待僵尸被收割」的语义需求（现无），需另行显式判 Z。
- 冷启动残留预检在最坏情形多付 ~0.8s（5 轮全活才报 Unreapable），换取消除 17s 级误停摆。
- spawn 日志 +2 行/尝试、holder 页 +1 定时器（纯展示）：日志与页面的增量可忽略。
- holder 页计时器禁令钉子随决策 4 细化：`setTimeout` 全禁与「重试链零定时器」不变；
  `setInterval` 由全禁改为仅许展示计时 1 处（`DshLoopbackProxyTests` 同批改写）。
- 待真机复验：下次壳异常退出场景，观察日志出现「杀后第 N 次复查确认死亡」且不再停摆。
