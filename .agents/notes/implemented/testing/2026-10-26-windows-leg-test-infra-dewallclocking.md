# Agent Note: windows 腿测试基础设施去墙钟化

Status: implemented

## Problem

0.7.2 bump 的两轮 CI 在 windows-latest 上红过两次且落在不同测试上（HANDOFF 记录，未留 run 号与失败测试名）；【推断 · 未证】这两次红的共同根因在测试基础设施、而非产品——代码在该窗口内未变。缺陷本身不依赖那条推断即可复现：两处都在「用墙钟窗口度量不保证发生的事」，断言形态与实现代码直接可读：

- `RuntimeSupervisorTests` 把 `CancellationTokenSource(300ms/500ms)` 当轮数预算：轮数 = 窗口 ÷ 每轮延迟，而每轮实际耗时远大于 `Task.Delay` 请求值（windows 定时器粒度叠加 runner 负载）。断言是吞吐门槛（`lockRounds >= 5`、`Order.Count >= 2`），窗口内轮数不足即误红。前批记录的单次实跑 929ms 对 300ms 预算即此形态【探索性：n=1，前批记录，本批未复测】；「windows 定时器粒度叠加 runner 负载」是解释该数值的成因推断，未单独立项取证。
- `ExternalFileLockHolder.Start` 用 10s 预算等子进程输出 `HELD`：powershell 冷启动在 CI 上可越过该预算；且 stderr 未重定向，失败只剩「未在 10s 内就绪」/「读到：`<null>`」，无法区分启动慢、脚本崩、还是锁失败。

## Decision

- 监督器测试改为信号驱动：fake host 在退出信号用尽时置 `Parked`，测试等该信号后取消（`s_parkTimeout=5s` 兜底「等信号」，`s_hangGuard=10s` 施于 `RunAsync` 的收口 `await run.WaitAsync(s_hangGuard)`——循环不响应取消才是挂死），重试延迟固定 `TimeSpan.Zero`。轮数因此只由 `ExitSignals` 决定。
- 断言全部精确化：`ExitSignals=1` → 记序恰为 `["recovery","recover:<url>"]`（并钉 stderr 尾部落盘日志）；残留锁死恰 1 轮且 `RestartCalls==0`；`ExitSignals=6` + 步长 3 → 恰 6 轮锁死、恰 3 条同因留痕（旧形态「同文日志数严格小于锁死轮数」随之作废，[pid-reuse-foreign-cmdline-clear](../bug-fix/2026-09-29-pid-reuse-foreign-cmdline-clear.md) 的 Testing 已同批改写）；无 URL 恰 3 轮（3 次失败面/3 次重启/3 条日志）；异常路径恰 2 次失败面 + 1 条「恢复失败」+ 1 条「重启未给出 URL」。
- 锁持有者：就绪预算 10s→30s（`s_readyTimeout`，注释记 rationale）；两个 OS 分支都 `RedirectStandardError`，启动即 `ReadToEndAsync` 持续排空（防管道互锁）；失败消息统一带「就绪预算 + 失败时进程状态与退出码 + stderr 尾部（末 20 行、≤800 字）」；`Stop()` 公开契约不变（Kill → WaitForExit → Dispose）。
- 不给 `RuntimeSupervisor` 加 TimeProvider 缝：本缺陷是「用窗口度量轮数」，不是延迟精度问题（见 Alternatives）。

## Alternatives considered

- **给 `RuntimeSupervisor` 注入 TimeProvider + 虚拟时钟**（`DshShellForward` 已有该形态）：落败——虚拟时钟解决的是「延迟时长是否按配置发生」，本批断言的是轮次与顺序，零延迟加信号停驻已完全确定；为此新增公共构造参数却没有任何测试消费它，是给将来留一份无消费者的 API 面。
- **保留墙钟预算只放宽（300ms→3s）**：落败——把误红概率换小而已，断言仍是吞吐门槛；负载再变就复发，轮数也依旧不精确。
- **锁持有者只放宽预算、不接管 stderr**：落败——预算再次被迫突破时仍只有一句「未就绪」，定位成本不变；持续排空 stderr 是让下次红能自证的最小改动。
- **改成同进程持有 `FileShare.None`**：落败——跨进程语义正是被测对象（同进程持有不构成他实例证据），该盲区记录在[跨进程文件锁语义](2026-09-13-cross-process-filelock-semantics.md)。

## Consequences

- 两处红从此指向真实回归：轮次与顺序被钉死，节奏变化会红在具体断言名上，而不是随机超时。
- windows 特有面仍只在本机之外验证：powershell 分支（30s 预算、stderr 排空、`FileShare.None` 失败消息）在 Linux 上无法执行，首次实证 = run `40352264564` windows 腿全绿（Core 209/209、Infrastructure 593 通过 19 跳过，含监督器五例与跨进程锁三例）；单次绿不构成概率证明，机制上墙钟依赖已移除。若 powershell 在 `HELD` 前挂死，消息形态是「失败时仍在运行、已终止（退出码 -1）」且 stderr 很可能为空。
- 成功路径的 stderr 排空 task 不消费结果：`Stop()` 的 Dispose 与它在极端时序下可能让它以 `ObjectDisposedException` 结束。.NET 对未观察异常默认不终止进程，反复实跑未出现（残留风险，不是静默失败面）。
- 单测时长下降：改前五个监督器用例按墙钟预算凑轮数（预算合计 3.4s，属配置值而非实测）；改后零延迟 + 信号停驻，5 例单次实跑 151–218ms（n=4，本机 Linux）。
- 用例数未增删：基线值见 [`scripts/test-baseline.json`](../../../../scripts/test-baseline.json)，本批不触发跟值。

## Testing

- `dotnet test tests/DeepSeek.Harness.Desktop.Core.Tests --filter RuntimeSupervisorTests` → 5/5（连跑 4 次全绿，151–218ms）；CI run `40352264564` windows 腿 Core 209/209 全绿。
- `dotnet test tests/DeepSeek.Harness.Desktop.Infrastructure.Tests --filter "StalePackagePrunerTests|ReleaseAssetTests"` → 42/42（含跨进程锁持有者三例）。
- 两工程 build 仅环境性 NU1900（本机 NuGet 缓存只读），无代码警告；`dotnet format --verify-no-changes --severity warn` 对两文件 exit 0。
- 失败诊断实证（Linux/python3 分支，传不存在父目录）：消息含「就绪预算 30s、失败前已退出（退出码 1）、stderr 尾部 Traceback/FileNotFoundError」。
