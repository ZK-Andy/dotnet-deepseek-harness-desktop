# Agent Note: 首选端口注定失败的启动尝试前置探测压缩

Status: proposed

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

端口被占时的首选端口启动尝试要走完 dsh 的完整插件树加载才暴露失败：2026-09-12 实机验收 ③（无关占位者抢端口）从尝试开始到检出 EADDRINUSE 实测 42–43s（三次），加回退 spawn 共 54s 用户可感延迟。

**2026-09-13 debug 模拟定位（隔离 DSH_HOME + 占位者 + stderr 全程打时间戳，dsh alpha.2 + dotnet-desktop profile 复制件；同条件两 run）**：dsh 在 bind web 端口之前要先加载整棵 cordis 插件树（`Promise.allSettled` 100+ loader entries），bind 是插件树加载的末段动作——run A 签名在 46.8s 出现，run B 全程 44.5s（exit=1，bind 失败即退、无尾部悬挂）；两 run 之差即插件树加载的 run 间波动，序列恒为「加载 → bind → 签名 → 立即退出」。即壳侧 stderr 签名检测是即时的，慢在 dsh 还没走到 bind；与 ③ 的 42–43s 同量级（模拟的 profile 是复制件、环境不同，不苛求逐秒对账）。`HarnessRuntimeHost.cs` 三处注释（「签名 1–2s 内即到」「悬挂约 40s」两个失实表述，散布在 StartFailure.PortConflict/StartCoreAsync 失败回收行/WaitForUrlAsync remarks）与实测不符——1–2s 大概率来自无插件树的最小验证环境，40s 悬挂从未在本机复现。

结论：**等待压缩不能靠更早读到签名**（签名不可能早于 bind），只能让壳在 spawn 前就用自有探测判死注定失败的尝试。

## Proposal

spawn 首选端口前，壳做一次**自有 bind 探测**（`Socket` 以 `ReuseAddress=false` 试 bind 该端口，即时、无子进程）：

- **探测被占** → 本次尝试必败（TOCTOU 例外见 Consequences），跳过 spawn，直接进 `RecoverFromFailureAsync` 既有处置链（血统收养 → scope 残留收割 → 漂移回退），省掉整个 42–47s 的注定尝试。
- **探测空闲** → 照常 spawn（自由端口/接力窗口的现有语义不变：探测后 47s 窗口内被续任者抢 bind 的竞态与今天相同，由既有 EADDRINUSE 收养路径兜底）。

同批修正 `HarnessRuntimeHost.cs` 三处失实注释（StartFailure.PortConflict、WaitForUrlAsync remarks、StartCoreAsync 失败回收行）为实测口径，并把实测数据落 docs/cookbook（[调试] 踩坑）。

## Alternatives considered

- **等续任者 bind 信号替代抢端口**（待办原候选：以市场 helper 出现 ≈1s 为「接力进行中」信号）：落败为**主路径**——helper/续任者信号只在血统接力场景存在，无关占位者（实测 ③ 的 python3）没有信号可等；bind 探测对两类场景统一生效。接力场景的竞态窗口与今天等价，无需单独信号通道。
- **缩短 WaitForUrlAsync 的 timeout**：落败——timeout 是 URL 时限不是冲突时限，缩了会误杀慢启动的正常实例；且注定失败的尝试仍白付一个 spawn。
- **复用 stderr 尾部历史签名**：落败——签名只来自本次 spawn 的流，无历史可读；且签名时刻 = bind 时刻，读到了也已付出 47s。
- **上游 dsh 改为先 bind 再加载插件树**：价值最高但属上游跟进（同 dsh SIGTERM 收敛一并提 Discussions），壳侧不依赖其落地；落地后本探测自动退化为无害快路径。

## Consequences

- 收益：③ 类场景用户可感恢复时间 54s → ~13s（省 42–47s 注定尝试；剩余为回退 spawn 的 12s）。
- 代价：探测与 dsh 实际 bind 之间存在 TOCTOU 窗口——探测空闲、spawn 后被占的竞态与今天完全相同（既有收养链兜底），不新增风险；探测被占但占位者恰在毫秒内退出则少付一次「其实能成」的尝试（概率极低，后果 = 直接漂移一次）。
- 非目标：回退 spawn 的 12s（dsh 正常启动时间）不压缩；漂移本身（localStorage 会话态丢失）不因本改动变化。

## Testing（拟）

- 纯逻辑：bind 探测函数对「空闲端口 / 被占端口 / 无权限」三态的判定（注入式探针，xunit）。
- 行为级：StartInnerAsync 在探测被占时**不 spawn**（进程计数断言）且直接走处置链；探测空闲时行为与现状逐字节一致。
- 实机：复刻 ③ 场景（无关占位者），断言「尝试开始 → 漂移完成」显著小于 42s。

## Related

- [端口漂移与 IPC origin 错配](../../implemented/bug-fix/2026-09-12-port-drift-ipc-origin-mismatch.md)：漂移后果与回退路径的行为事实源。
- [运行时交接收养](../../implemented/bug-fix/2026-09-12-runtime-handoff-adoption.md)：探测被占时收养分支的既有语义。
- [dsh 沙箱子进程孤儿泄漏](../../implemented/bug-fix/2026-09-12-dsh-sandbox-child-orphan-leak.md)：上游跟进面（bind 时序可与之合并提 Discussions）。
