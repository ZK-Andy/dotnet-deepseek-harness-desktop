# Agent Note: 入口桥两相化（异步 I/O 相 + thread 0 同步相）

Status: implemented

Erratum: 2026-10-18 — 正文「端口收成两方法」的表述失效：`IStartupSequence` 已删除（零消费者，ADR compose-root-lifetime-and-config-single-source 的 D5），编排触发改用具体类型；其余判断与决定不动。

Review: FULL/2026-10-15#6/R1=ok R2=ok R3=ok

Related: 母篇 [composition-mechanism-container-assembly](2026-10-08-composition-mechanism-container-assembly.md)（其「同步桥只余起步一处，`await` 贯通另立条目」由本篇承接）；排序器形状 [startup-sequencer-shape](2026-10-15-startup-sequencer-shape.md)；退出编排前置 [app-exit-orchestration-core-usecase](2026-10-15-app-exit-orchestration-core-usecase.md)；解析面 [sequencer-resolution-injection](2026-10-15-sequencer-resolution-injection.md)。

## Problem

排序器唯一的同步桥是 `StartRuntime` 里的 `_runtimeStarter.StartAsync(...).GetAwaiter().GetResult()`——母篇把「全 async 化」挂成另立条目，C6 也给它留了一行白名单。两个约束把它钉死：

- **Ryn `RunAsync` 必须在 thread 0**（`Ryn.Core/RynApplication.cs:75-108`：worker/pool 线程调用直接抛），主循环同理。故「`Run()` 全 async + 桥留接线层一处」不可行：`await` 之后的续体在池线程，`App.Run()` 会从池线程调 Ryn。
- **桥不能留在排序器**：它是「排序器只按序触发」形状契约里的唯一例外（C6 桥帽 1），而例外一旦常驻，形状闸对排序器内部阻塞面就失去了断言力。

## Decision

1. **两相切分（切分点 = 唯一 await）**：`RunStartupAsync()`＝异步相，只装 I/O 起步链（profile 前置 → 宿主产出 → 随包插件安装 → `await _runtimeStarter.StartAsync(...).ConfigureAwait(false)`）；`RunMainLoop()`＝同步相（thread 0），承接宿主解析面命中、Ryn 面尾段（引导接回 / 托盘 / 监督 / 健康 / 顶栏 / 更新 / 告知）、主循环与退出编排。`RunStartupAsync` 的续体（池线程）只做方法收尾。
2. **Ryn 面调用一律留 thread 0**：异步相不含任何 Ryn 面语句（托盘、顶栏注入、主循环）。Ryn 服务的线程亲和未在本机可测范围，切分点取唯一 await 使本批**零新增线程亲和面**——只是把既有的桥从链中段挪到链外。
3. **入口桥移到接线层恰好一处**：`DesktopAdapter.Run` 内 `sequence.RunStartupAsync().GetAwaiter().GetResult(); return sequence.RunMainLoop();`。`GetAwaiter().GetResult()` 不包 `AggregateException`，起步异常照旧原样外抛（与搬前语义一致）。
4. **闸改向**：C4 由「根上禁桥」改「根上恰一处」（计数 ≠ 1 即 violation——零处意味着相位切分搬出了既定账本，多处意味着桥又爬回内部）；C6 桥帽 1 → 0，且入口方法集从 `Run()` 扩到**全部 public `Run*`**（`RunStartupAsync` 与 `RunMainLoop` 同受「无控制流、无域文本、不构造自有类型」约束）。
5. **端口形状**：`Core.Bootstrap.IStartupSequence` 收成相位对齐的两方法（唯一消费者是接线层）。宿主在同步相经 `_resolutions.Host.Value` 二次命中（Lazy 缓存同一实例、无 I/O），首个解析点仍在异步相。
6. **母篇口径同步**：母篇 Decision 1 与 Consequences 的「同步桥只余起步一处 / 全 async 化另立条目」按承接关系更新指针，不再把它留成悬案。

## Alternatives considered

- **`Run()` 全 async + 桥留入口一处（落败）**：续体在池线程，`App.Run()` 与 Ryn 托盘/顶栏面随之落到池线程——`RunAsync` 直接抛，这是题面的硬边界。
- **桥留排序器、C6 帽保持 1（落败）**：形状债与 C4 的「根上禁桥」互相矛盾（桥总得在某一侧），且排序器内部阻塞面永久免检。
- **异步相连尾段一起装（托盘/顶栏在池线程）（落败）**：把 Ryn 面线程亲和押在「只能实机验」的一次全链实机上，收益只是少一次 Lazy 命中；零新增亲和面严格优于押注。
- **`RunStartupAsync` 返回 `Task<Task>` 让根把监督任务递给同步相（落败）**：跨相值只此一件且是运行期 `Task`，为它把端口签名做成嵌套任务、把阶段值通道重新打开（`SEQUENCER_VALUE_TYPES` 空集是刻意不变量），代价高于收益；宿主经 Lazy 二次命中的写法不引入新通道。
- **改上游让 Ryn 暴露 thread-0 调度（落败/上游面）**：那才是全 async 的正解，但属上游依赖，不在本仓可决策范围。
- **把两相都放进接线层（落败）**：编排搬进根等于回退 form separation（C2 方法清单即拦）。

## Consequences

- 买到的：排序器零同步桥、根上恰好一处（C4 机械可证）；两相职责按「I/O vs Ryn 面」可读，相位由端口签名钉住；退出编排（承接篇）有了 thread 0 的同步相作为归属。
- 付出的：同步相多一次 `_resolutions.Host.Value`（Lazy 命中）；`IStartupSequence` 端口从单 `Run()` 变两方法；C6 桥帽归零后，将来若真需要链内阻塞必须走 ADR 重开。
- **实机验收 ✅（2026-10-15，随 v0.6.9 装机）**：续体在池线程 + 根上 `GetResult()` 的线程行为与两相切换下的冷启动/引导/收养恢复/失败面全链在真实桌面走通——判据全中：`~/.dsh/logs/host.log` 有页面健康行、无连续导航/提交超时降级、恢复后窗口回代理根。

## Testing

- `python3 scripts/verify-compose-root.py --self-test`：C4 三夹具（基座恰好一处通过 / 第二处 flag / 零处 flag）＋ C6 夹具（桥帽 0：单个桥即 flag；两个入口名：`RunStartupAsync` 控制流、`RunMainLoop` 引号文本；**静态修饰符入口**同样被抓；**表达式体入口不吞下一成员**的跨成员作用域）——全绿（24 条）；`--enforce` 对本仓报 `root new-of-owned count: 6`、无 violation。
- `CompositionRootSequenceTests` 两相链序重订（容器前头部：`CreateInstance` → 桥 → `RunMainLoop()`；排序器：`RunStartupAsync` 四步 → `RunMainLoop` 尾段九步 → `_exit.Run(`）。
- `dotnet build` 0 错误；`DeepSeek.Harness.Desktop.Tests` 229/229、`DeepSeek.Harness.Desktop.Core.Tests` 229/229（含新增退出编排四用例）。
