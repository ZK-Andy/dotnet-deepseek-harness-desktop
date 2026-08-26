# Agent Note: page-health-monitor

Status: implemented

## Problem

「进程活着、页面死了」类事故（companion 缺 `apply` 致整页白屏、更新区块 ReferenceError 永久降级等历史三起）全部靠用户实机人肉发现——`RuntimeSupervisor` 只订阅子进程退出事件，dsh 存活但页面空白完全不可见。对照 anywhere-labs/dsh-desktop（2026-08-26）确认其有渲染进程健康监控面。

## Decision

**阶段 1：只做「看见」，不接任何自动恢复动作。**

1. **探针住在宿主不在插件**：`PageHealthMonitor` 由壳侧定时对 WebView 跑一条只读表达式（body 有子节点即 alive），不注入脚本、不改页面、零 IPC——历史上 companion 自身就是事故源，探针住进插件会在最需要它的时候一起死。
2. **判定核心纯逻辑可单测**：`PageHealthTracker` 连续 3 次 Dead 才宣告迁移（导航空窗与渲染间隙去抖）；Unknown 不计数也不清零；任意 Alive 立即复位。锚点用 body 子节点数而非 dsh 内部组件结构——上游 DOM 演进不致探针失效。
3. **产出 = 迁移日志 + 诊断快照**：状态翻转经 `[health]` 行进 host.log；最新快照经惰性委托在导出时刻求值，收录进诊断包 `state.txt` 的 `health` 行。阶段 2 是否接线自动裁决（超时→恢复屏/重启），由阶段 1 积累的误报/漏报数据决定。

## Alternatives considered

- **阶段 1 即接自动重启裁决**：落败——误报引发的重启循环比白屏更伤害可用性；阈值与退避策略没有数据支撑前上线属赌博。
- **探针注入页面内 setInterval + IPC 上报**：落败——注入脚本依赖桥接就绪且与导航竞速，宿主轮询 EvaluateJavaScript 天然免疫页面生命周期；上报通道反而多一层依赖。
- **探针锚定 dsh UI 根组件选择器**：落败——上游前端演进会漂移选择器制造假阴性；body 子节点是引擎层事实。
- **MutationObserver 常驻监听**：落败——常驻观察者的内存与 CPU 开销换不来比 10s 轮询更多的信息量，观测场景无需实时。

## Consequences

- 已知边界：dsh 崩溃后恢复页本身是壳的文档（有内容），此阶段按 alive 记录——该时段进程监督已有独立信号，本决策的靶心是「dsh 在跑但页面空白」。
- 10s 轮询间隔下最长约 30s（3 连击）延迟才宣告 dead——观测定位向，不追求秒级。
- 阶段 2 未立项：接线条件 = 阶段 1 日志出现真实 dead 且无误报记录；届时的动作候选为切换恢复屏（非重启）。

## Related

- [shell 可观测性与诊断](../architecture/2026-08-24-shell-observability-diagnostics.md)：state.txt 快照与 host.log 出口的既有契约，health 行挂载于此。
- [diag-masking-and-recovery-page](../bug-fix/2026-08-26-diag-masking-and-recovery-page.md)：同批落地的恢复页三件套——阶段 2 若接「切恢复屏」动作将复用其文档形态。
