# Agent Note: 铸币稳定化门控接 TimeProvider 测试缝——消灭墙钟竞速型误判

Status: implemented

## Problem

`DshShellForwardTests.Mint_WebFaceReadyAfterDelay_PassesGateWithoutFailOpen` 跨环境失败两次（本地复现 1 次 + CI ubuntu 1 次），失败形态一致：稳定化循环走了 fail-open 分支而非「稳定窗达成」放行，成功日志缺失。这不是 flaky 巧合，而是测试设计的结构性缺陷：**「稳定窗达成 vs 预算耗尽」的分叉被压在墙钟竞速上**——探活实际耗时（桩 30ms 延迟 + xunit 全量并行下的调度抖动）与 250ms 预算赛跑，负载一重即误判。测试注释里「全量并行下 100ms×2 会顶穿预算（实测 flaky）」证明此前已用调小延迟的方式压过一次——那是打补丁，赔率还在。生产门控本身无缺陷：fail-open 本就是预算耗尽时的既定语义，错的是测试把「哪条分支被走到」交给了负载。

## Decision

- **生产侧**：`DshShellForward` 增加内部测试缝 `TimeProvider`（构造可选参，缺省 `TimeProvider.System`）；稳定化循环（`WaitWebFaceStableAsync`）的时刻全部改经提供者取——`GetTimestamp`/`GetElapsedTime` 起点、`RelayWebReadinessGate.Observe` 的 `GetUtcNow`、轮询节拍 `Task.Delay(TimeSpan, TimeProvider, ct)`。生产行为零变化（System 提供者即墙钟原语义）。
- **测试侧**：新增 `VirtualTimeProvider` 夹具——仅 `Task.Delay` 的等待推进虚拟时间（dueTime 满量 + 即时回调），探活（真实回环 HTTP/WebSocket）不占虚拟时间。断言稳定化分支走向的 7 例测试全部换虚拟时钟：通过路径两拍虚拟耗时 10ms 远小于预算（恒走「稳定窗达成」）、fail-open 路径恰好 N 轮预算耗尽（轮数确定）——两条路径的触发不再依赖墙钟，压测（夹具 5 连跑 + 全套件 3 连跑）与负载无关地稳定。

## Alternatives considered

- **继续调大预算/调小延迟（落败）**：历史上的做法，本次失败证明赔率只是被调低而非消除；CI runner 负载无上界，任何墙钟裕量都可能在某次并行峰值被顶穿。
- **测试加重试/移除分支区分断言（落败）**：retry 掩盖缺陷、删断言丢覆盖——两者都把「测试在测什么」变模糊，与 fail loud 相悖。
- **只测纯门控 `RelayWebReadinessGate`、放弃循环集成测试（落败）**：门控纯逻辑早有单测，真实缺口恰在「循环 + 探活 + 门控」的集成路径（probe→observe→预算三者交互），删集成测即丢掉该缺口仅有的覆盖。
- **手写实时队列式定时器模拟（放弃）**：虚拟时钟仅需「Delay 推进即完成」，实时回调队列徒增复杂度且无断言价值。

## Consequences

- 买到的：稳定化门控的分支覆盖确定性化，CI 不再随负载翻红；fail-open 测试从真实等 250ms 预算变为瞬时完成；`TimeProvider` 缝为后续任何时刻敏感路径的测试化提供同型入口。
- 付出的：`DshShellForward` 多一个可选注入面（internal，生产无感）；`VirtualTimeProvider` 的「Delay 即推进」语义要求后续使用方理解「探活不占虚拟时间」——稳定窗/预算只按轮询节拍计数，夹具注释已钉。
- 测试快参 `s_fast` 的大小从此只是语义表达，不再承担竞速赔率；原「节拍不宜再小防探活风暴」的约束对虚拟时钟测试失效，但参数现状不动（语义不变即不改）。

## Related

- [mint-epoch-mux-gate](2026-10-02-mint-epoch-mux-gate.md)——稳定化门控的语义来源；本篇只改其测试面，决定不动。
- [holder-mint-gate-deepening](../feature/2026-10-01-holder-mint-gate-deepening.md)——铸币就绪门深化的同族决策。
