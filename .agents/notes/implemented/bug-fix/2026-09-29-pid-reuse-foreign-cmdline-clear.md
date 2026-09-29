# Agent Note: 残留锁死补 PID 复用甄别与监督日志节流

Status: implemented

## Problem

冷启动孤儿清扫的「活着但验不明 → 不敢杀、不 spawn（fail loud）」在 PID 复用场景下永不自愈。实机事故
（2026-09-29，0.5.9 rpm）：上一轮会话把 dsh pid=3239 写进 `.dsh-pid`，18:25 机器重启后 GNOME 的
`/usr/libexec/localsearch-3` 恰好复用该 PID；壳 18:27 自启动读 stale 记录，token 复验不中、进程又活
（是 localsearch-3），按 residue-lock-fail-loud 判 Unreapable 拒绝 spawn。localsearch-3 常驻，锁本轮
开机内不会解除；监督器又以 1s 节拍无退避重试，同文 fail-loud 日志连刷 1111 条，恢复屏永久锁死。
原设计注释把「PID 复用指向无关进程」与「Windows/macOS 无 /proc 复验」并列为验不明，但两者性质不同：
前者可证原记录已过期（记录本身是脏数据），后者才是真不可判定。

## Decision

两点，均不影响零误杀语义：

1. **PID 复用甄别**（`OrphanDshReaper.EnsureNoResidue` 新增可选 `readCommandLine` 注入，生产接
   `RuntimeLineageProbes.ReadCommandLine`）：「活着但验不明」时再读一次 `/proc/<pid>/cmdline`（全局
   可读，他人进程也能读，与 environ 的属主限制不同）——命令行可读且 `OrphanDshReaper.PlausiblyOwnRuntime`
   判为异己（argv[0] 既非 node 也非 dsh，后段也无 dsh 脚本）即证原记录过期：清掉脏 `.dsh-pid` 并按
   Clear 放行 spawn，绝不杀该进程。甄别不了（cmdline 不可读/形态命中白名单/委托异常）保持 Unreapable
   fail loud。白名单取窄不取宽：把「像」误判成自家只是放弃一次放行，把「不像」误判成他者会跳过真实
   残留。生产 dsh 为 node 脚本（shebang），argv[0] 恒 node，无关系统服务必不命中。
2. **锁死重试日志节流**（`RuntimeSupervisor`）：残留锁死的重探每轮照跑（`SupervisorFailedRetryDelaySeconds`
   1s 不变，用户手动清理后仍秒级恢复），但同因留痕（退出行 + stderr 尾巴 + 锁死行）只在首轮与每隔
   `SupervisorBlockedLogEveryRounds`（新配置键，默认 60 ≈ 每分钟一条）轮各留痕一次；解锁即清零计数，
   普通崩溃轮的日志行为不变。

## Alternatives considered

- **spawn 记录带 boot-id，跨重启直接判过期**：一劳永逸且不依赖进程形态匹配，但改磁盘记录格式（升级
  窗口内新旧版互读需兼容层），而本次事故只用「cmdline 明显异己」即可解，格式变更另立项。
- **验不明一律放行 spawn**：靠 spawn 前的 bind 探测兜端口冲突。落败：真实孤儿（Linux 可杀可验之外
  的残余形态）会照撞端口，且违背 residue-lock-fail-loud 的 fail-loud 契约。
- **监督器对锁死轮做指数退避**：一并解决日志刷屏与空转，但用户手动清完残留后最坏要等一个退避上限才
  恢复，恢复延迟回退；节流只压日志、不碰重调节拍，行为面更窄。

## Consequences

代价：甄别白名单耦合「dsh 经 node 执行」这一运行时形态（dsh 改非 node 宿主时需同步放宽/收紧白名单，
已钉 `PlausiblyOwnRuntime` 单测）；`EnsureNoResidue` 多一个注入参数（既有调用点缺省不传，行为不变）。
收益：PID 复用类事故开机自愈（无需手动删 `.dsh-pid`），真验不明残留仍 fail loud；锁死轮日志从秒级
连刷降为分钟级心跳。

## Testing

`OrphanDshReaperEnsureTests` 新增五钉：异己 cmdline → Clear + 脏记录被清 + 不杀；node 形态 → 保持
Unreapable + 记录保留；cmdline 不可读/委托异常 → Unreapable；`PlausiblyOwnRuntime` 白名单形状。
`RuntimeSupervisorTests` 新增节流钉：锁死轮数不因节流减少、首轮与步长轮留痕、同文日志数严格小于锁死
轮数。

## Related

- ADR residue-lock-fail-loud（本笔记在其 Unreapable 语义上开唯一放行例外）
- ADR self-update-exit-reaps-dsh-child（`.dsh-pid` 记录路径的来源）
