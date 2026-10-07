# Agent Note: PagePump 重试助手统一为单点

Status: implemented

Review: FULL/2026-10-07#9/R1=ok R2=ok R3=ok

## Problem

`PagePump` 承载四路「就绪前重试」：横幅注入、顶栏注入、顶栏脚本注册、引导进度/预装状态推送。四者循环骨架相同（按节拍重试 `InvalidOperationException`、其余异常放弃），但各写各的：

- **语义漂移**：`ObjectDisposedException` 只在注册路先于 `InvalidOperationException` 捕获并放弃；另三路把它当「未就绪」处理，退出期会白跑：推送两路跑满预算（15 次 × 400ms）并多打一行「重试耗尽」，注入两路（横幅 30 次 × 1s、顶栏 15 次 × 400ms）则静默白跑（旧注入单点本身无耗尽行）。
- **预算口径分裂**：注册路与推送路共用 `PushMaxAttempts`（次数），而 mac x64/Rosetta 的 webview 创建晚于按次数计的 6s 预算——注册路需要「等到点/到取消」，次数口径表达不出来（见 [frameless-uniform-caption-bar](../feature/2026-10-07-frameless-uniform-caption-bar.md)）。
- **留痕不一致**：横幅与顶栏注入失败时无耗尽行（静默丢弃），发版冒烟只能靠注册路那一行判断顶栏面。
- 同一循环骨架四份，下一处注入点还会再抄一份（HANDOFF 待办「PagePump 四个重试助手合并」）。

## Decision

单点 `RetryWhenReadyAsync(op, onGiveUp, maxAttempts, retryDelay, ct)` 承载全部四路：

- **次数口径为主**：`maxAttempts` + `retryDelay` 正是这三类配置键（`BannerMaxAttempts`/`PushMaxAttempts`/两条节拍键）的既有语义，直传入单点；`maxAttempts ≤ 0` 即不尝试（配置把次数配成 0 = 停用该路），`retryDelay ≤ 0` 即不等待（尝试背靠背）。
- **到点只在注册路出现**，由 `AttemptsForBudget(deadline, tick)` 单点折算成次数：`ceil(到点 / 节拍)`，不读墙钟（故单测确定可复现）；到点非正值 = 停用该注册路，节拍非正值 = 预算不可度量、按单次尝试（防热循环），商夹到 `int.MaxValue`。配置面只新增秒键 `CaptionBarRegisterSeconds`。
- **异常分派单点化**：`ObjectDisposedException`（`InvalidOperationException` 子类，须先捕获）→ 终止；`InvalidOperationException` → 按节拍重试；其余 → 交 `onGiveUp` 留痕后终止。
- **终态三值**（成功／次数用尽／终止）：调用点只区分前两者（成功行与耗尽行），「终止」是已销毁、其余异常（已由 `onGiveUp` 留痕）与取消的合并态——三者对调用点不可区分，均不再记行（退出期常规路径，逐次留痕是噪音），该「无痕」由枚举文档显式写明。
- **留痕**：四路耗尽都记行（横幅与顶栏注入此前静默，新增两行）；发版冒烟依赖的 `[caption-bar] 每页加载脚本已注册（同 URL 重载不再依赖导航事件）` 与 `[caption-bar] 每页加载脚本注册重试耗尽（` 前缀形状不变。

## Alternatives considered

- **预算以 `TimeSpan` 到点表达、由「次数 × 节拍」派生出单点预算（首版形态，评审落败）**：助手收 `budget` 再 `ceil(预算/节拍)` 还原成次数，对三条派生调用点是恒等往返，却引入两处缺陷——① 节拍为 0 时派生预算塌成 `TimeSpan.Zero`，该路 0 次尝试并打出误导性的「重试耗尽」（横幅、顶栏注入与两条推送全静默失效，而基线是 N 次即时尝试）；② 极端但可解析的配置下 `次数 × 节拍` 乘积超 `TimeSpan` 上限，构造期即抛 `OverflowException`，落在 fire-and-forget 任务上等于静默失败。次数口径直传 + 注册路单点折算同时消掉两处。
- **助手读墙钟（`Stopwatch`）判到点**：更贴「真实到点」；但这里的等待本就由节拍驱动，折算已等价，读墙钟只会让单测失去确定性（要吃真延迟或注入时钟）。
- **接 `TimeProvider` 测试缝**（仓库已有先例，见 [mint-gate-virtual-time-seam](../bug-fix/2026-10-05-mint-gate-virtual-time-seam.md)）：那处是墙钟竞速型误判必须虚拟化；本处无墙钟参与，为表达预算而引入缝，成本大于收益。
- **只给注册路补到点、四份循环保留**：diff 最小，但语义漂移与口径分裂原样留在仓里，且下一处注入点还得再抄一份——合并挂账正是为此刻。

## Consequences

- 买到的：重试语义与留痕单点改、五处重试点同享；「已销毁即放弃」对全部注入路生效（退出期不再空跑满预算）；耗尽在四路都可见。
- 付出的：日志面新增两行（横幅/顶栏注入耗尽）——按「无此行」判健康的下游判据需同步；到点按节拍折算而非墙钟，某次注入调用自身挂死仍会拖住该路（与今日同性质）；「退出期已销毁」四路静默（与旧注册路的「重试耗尽」一行不同，判别时以「已注册」行缺席 + 无耗尽行为准）。
- 组合根接线（`StartupSequence.SetupCaptionBar`）新增的「首次导航到达补注册」一次性闩属注入时机条，记在 [frameless-uniform-caption-bar](../feature/2026-10-07-frameless-uniform-caption-bar.md)，不在本篇。
