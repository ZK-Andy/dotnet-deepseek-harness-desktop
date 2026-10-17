# Agent Note: launcher-probe-log-classification

Status: implemented
Review: FULL/2026-10-18#4/R1=ok R2=ok R3=ok

## Problem

host.log 里反复出现 `[host] 单实例请求忽略：`——冒号后为空。本次取数（2026-10-18）：`grep -cE '\[host\] 单实例请求忽略：\s*$' ~/.dsh/logs/host.log` = 26 条，首条 2026-08-28 21:47:56、末条 2026-10-18 06:45:26；计数随启动次数累积，不是稳定口径。成因不是措辞笔误，而是分类错误：`PrimaryListener.ServeAsync` 只能看到「读到的一行」，而单实例探活（`BindWithStaleRecovery` → `ProbeAlive`）只 connect、不发一个字节，服务端 `ReceiveAsync` 返回 0 字节，于是被当成「读到空命令行」处理——先记 `[host] 单实例请求：`（空），再记 `[host] 单实例请求忽略：`（空）。探活是协议内的连通性判定，不是请求；日志却把它写成「有个请求被忽略」，且每次残留自愈就刷两条。

## Decision

- **0 字节读 = 探活连接**：直接返回，不记「请求/忽略」行。探活没有可执行动作；残留自愈路径已有「单实例锁已持有」「单实例监听不可用」「残留锁清理失败」三类行，足以判读。
- **读到 ≥1 字节但 `Trim()` 后为空**（连上只发换行）= 畸形输入：仍记到达痕，冒号后用 `<空>` 占位。
- 其余非 `show` 输入照旧按原文记录并断链，`show` 路径的「请求：show → 处理完成」行不变。

## Alternatives considered

- **只补占位（`<空>`）、不改分类**：落败——探活仍每开一次刷两行，且「请求被忽略」这个措辞本身就是错的，占位只掩盖噪声。
- **探活也记一行（如「探活连接」）**：落败——探活是启动路径的例行内部动作，记它等于给噪声换个名字。
- **全部静默（畸形/非法输入也不记）**：落败——到达性留痕的目的正是区分「到了被忽略」与「没到」，非法命令必须留痕。

## Consequences

- host.log 不再因残留自愈长出空载荷行；代价是探活失去独立留痕。
- 已知盲区：0 字节读同样覆盖「二启连上后在 send 前崩溃/断线」——服务端无法把它与探活区分，两类都不留痕。**后者两侧都没有日志可查**（二启死在 `NotifyPrimary` 落「通知未达」之前，主实例侧按本决策也不记），只能靠第三方观察瞬时连接表（如 `ss -x | grep deepseek-harness-desktop`，覆盖正式 / `.dev` / `-<uid>` 三种 socket 名）。这是相对旧实现（两条空载荷行）的真实留痕损失，为压掉例行噪声接受。仍可判的是「发过 `show` 但没等到 ack」：二启侧落 `DesktopAdapter` 的「已有主实例在运行（launcher 二次启动）…通知显示主窗未达」，主实例侧只有「请求：show」而没有「处理完成」。
- 可观察变化只落在非 `show` 输入：空白载荷行由「冒号后为空」变 `：<空>`，0 字节连接完全不产生行。
- 协议面未变：连接仍是一次一行命令、`show` 回 `ok`，其余不断链。

## Testing

- `LauncherActivationTests` 全绿（计数唯一家是 `scripts/test-baseline.json`，不在笔记里抄）：新增 `Serve_ProbeConnection_LeavesNoRequestTrace`（探活后日志为空、随后合法命令仍到达并触发回调）、`Serve_WhitespacePayload_IsLoggedWithEmptyPlaceholder`（两行都带 `：<空>`）；两者都用**封闭集相等断言**（整条用例的日志行全量比对）。探活的确定性屏障：客户端连上后只半关发送方向，服务端读到 0 字节即返回并 dispose，客户端读到 EOF 才算它处理完——不靠时间窗判负（单纯「连上即断」的探活无法被观察，客户端会自己先关）。既有 `Serve_IgnoresForeignPayload_KeepsListening` 继续钉「垃圾/空白载荷不断链、合法命令仍可达」。
