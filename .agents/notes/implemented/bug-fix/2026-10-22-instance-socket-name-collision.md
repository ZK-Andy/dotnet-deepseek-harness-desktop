# Agent Note: 实例 socket 改名避开 dsh 同名锁

Status: implemented

Review: LIGHT/2026-10-22/R2=ok（首轮 0 Blocker + 3 Suggestion 全采纳；验轮 0 Blocker + 2 Suggestion，按收口契约不开轮：changed 面纳入已暂存 diff、删去无落点的升级说明括号）

## Problem

单实例仲裁以 `$XDG_RUNTIME_DIR/deepseek-harness-desktop.sock` 为锁（[single-instance-launcher-activation](../architecture/2026-08-26-single-instance-launcher-activation.md)）：首实例 `bind` 成功即持锁监听；二启 `bind` 撞名后连接发 `show`、收到 `ok` 即退出。锁名是硬编码字面量 `deepseek-harness-desktop`，与 dsh 本体在同一 `XDG_RUNTIME_DIR` 下使用的 socket 同名。

2026-10-22 观测到该路径上确有监听者：`/run/user/1000/deepseek-harness-desktop.sock`（mtime 19:12，`ss -xl` 显示 `LISTEN`），同时段无桌面壳进程。持有进程不可见——沙箱 PID 命名空间内只有 5 个 pid，按 socket inode 扫 `/proc/*/fd` 无命中【推断 · 未证：监听者属 dsh 一侧；观测只证明该路径被非壳进程占用】。

后果是一条静默失败链：壳启动 `bind` 撞 `EADDRINUSE` → `ProbeAlive` 连得通（对面是 dsh）→ 判「已有主实例」→ `NotifyPrimary` 发 `show` → 对面不认这条命令 → 壳记一行日志后退出，窗口不出现。G0 的唤回回调是 no-op（[avalonia-g0-entry-switch](../architecture/2026-10-22-avalonia-g0-entry-switch.md)），连唤回都做不到；反向同理，dsh 侧也会把壳当同名实例。G0 实机取证必须隔离 `XDG_RUNTIME_DIR`，正是这条的临时回避。

## Decision

- 锁名改为 `dotnet-deepseek-harness-desktop`（`LauncherActivation.SocketAppName`）；dev 变体仍加 `.dev` 后缀，`XDG_RUNTIME_DIR` 缺失时仍回退 `<名字>-<uid>`。
- 两处路径解析测试钉新字面量（`XDG_RUNTIME_DIR` 在场与缺失回退两形态）。
- 不做 socket 迁移：旧名归 dsh 一侧，壳不持有、也不删非自有文件；壳自己旧名的崩溃残留没有读者，留给 runtime dir 的会话级清理。

## Alternatives considered

- **收进子目录（`$XDG_RUNTIME_DIR/dsh-shell/instance.sock`）**：多一条目录创建与权限失败路径，收益与加前缀相同——撞名只发生在同一层的同名文件上。落败。
- **掺 uid 做隔离**：`XDG_RUNTIME_DIR` 本身按用户分域，uid 后缀不带来额外隔离，名字仍与 dsh 相同。落败。
- **维持现状，要求启动前隔离 `XDG_RUNTIME_DIR`**：把系统级约束推给每个用户，实际效果是壳与 dsh 不能共存。落败。

## Consequences

- 与 dsh 共存启动不再误判主实例；二启唤回仍不工作（G0 回调 no-op，G1 补），但失败面从「静默退出、窗口不出现」变成「正常出窗」。
- 升级窗口有重叠期：旧版进程仍持旧名锁时，新版按新名 `bind` 成功，两个实例各拉一份 dsh 运行时并行到旧进程退出。不设迁移逻辑，接受这段重叠。
- 升级后旧名 socket 若仍存在，壳不再读它；若它由已崩溃的旧壳残留，没有进程会清理，只能等 runtime dir 的会话级回收。
- dev 与正式各持一把锁的隔离不变。

## Related

- [single-instance-launcher-activation](../architecture/2026-08-26-single-instance-launcher-activation.md)：锁机制本体的所有者。
- [avalonia-g0-entry-switch](../architecture/2026-10-22-avalonia-g0-entry-switch.md)：当前锁的消费入口（`TryBecomePrimary`）。
