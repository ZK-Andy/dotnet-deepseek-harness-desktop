# Agent Note: page-health-monitor

Status: implemented

## Problem

「进程活着、页面死了」类事故（companion 缺 `apply` 致整页白屏、更新区块 ReferenceError 永久降级等历史三起）全部靠用户实机人肉发现——`RuntimeSupervisor` 只订阅子进程退出事件，dsh 存活但页面空白完全不可见。对照 anywhere-labs/dsh-desktop（2026-08-26）确认其有渲染进程健康监控面。

## Decision

**观测已闭环，且有界自动恢复已接线。**

1. **探针住在宿主不在插件**：`PageHealthMonitor` 由壳侧定时对 WebView 跑一条只读表达式（body 有子节点即 alive），不注入脚本、不改页面、零 IPC——历史上 companion 自身就是事故源，探针住进插件会在最需要它的时候一起死。
2. **判定核心纯逻辑可单测**：`PageHealthTracker` 连续 3 次 Dead 才宣告迁移（导航空窗与渲染间隙去抖）；Unknown 不计数也不清零；任意 Alive 立即复位。锚点用 body 子节点数而非 dsh 内部组件结构——上游 DOM 演进不致探针失效。
3. **产出 = 迁移日志 + 诊断快照**：状态翻转经 `[health]` 行进 host.log；最新快照经惰性委托在导出时刻求值，收录进诊断包 `state.txt` 的 `health` 行。
4. **有界自动恢复（参考对齐批次五）**：连续 Dead 达阈值宣告后，在预算内触发一次有界 reload（reload 委托经 `PageHealthMonitor` 注入，靶点=记录的当前 dsh web URL），耗尽即转入观测-only（leave 自动恢复面），成功恢复（Alive）即复位预算窗口——误报引发的重启循环比白屏更伤害可用性，故恢复必须**有界**（见 [reference-alignment 批次五](../architecture/2026-08-29-reference-alignment.md)）。

## Alternatives considered

- **阶段 1 即接自动「重启」裁决**：落败——重启代价过高且误报会反复打散会话；改有界 reload（轻量，不重建进程），并保留观测优先。
- **探针注入页面内 setInterval + IPC 上报**：落败——注入脚本依赖桥接就绪且与导航竞速，宿主轮询 EvaluateJavaScript 天然免疫页面生命周期；上报通道反而多一层依赖。
- **探针锚定 dsh UI 根组件选择器**：落败——上游前端演进会漂移选择器制造假阴性；body 子节点是引擎层事实。
- **MutationObserver 常驻监听**：落败——常驻观察者的内存与 CPU 开销换不来比 10s 轮询更多的信息量，观测场景无需实时。
- **参照逐点等同的精确 boot 花屏信号**：落败——参照（iframe 模型）精确识别 `#root` 下「HARNESS + Loading plugins」花屏才报 stalled；我方（直载模型）以「body 无子节点即空白」为 Dead 信号，两者都捕获「进程在跑但页面没到应用态」的假活形态，恢复机致一致（有界 reload + leave 观测面），无需逐点模拟参照信号。

## Consequences

- 已知边界：dsh 崩溃后恢复页本身是壳的文档（有内容），此阶段按 alive 记录——该时段进程监督已有独立信号，本决策的靶心是「dsh 在跑但页面空白」。
- 10s 轮询间隔下最长约 30s（3 连击）延迟才宣告 dead——观测定位向，不追求秒级。
- 有界恢复随批次五落地：默认预算 3 次（协议型安全常量），耗尽转观测-only；reload 失败/取消被吞，绝不拖垮探针主循环。

## Related

- [shell 可观测性与诊断](../architecture/2026-08-24-shell-observability-diagnostics.md)：state.txt 快照与 host.log 出口的既有契约，health 行挂载于此。
- [diag-masking-and-recovery-page](../bug-fix/2026-08-26-diag-masking-and-recovery-page.md)：同批落地的恢复页三件套——有界恢复动作与崩溃恢复页共用同一导航面，但恢复页是「进程级崩溃」的信号，探针靶心是「页面级假活」。
- [reference-alignment 批次五](../architecture/2026-08-29-reference-alignment.md)：有界自动恢复（`PageHealthRecovery` + `PageHealthTracker.ReArm`）在此落地。
