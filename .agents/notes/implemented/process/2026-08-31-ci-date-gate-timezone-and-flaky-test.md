# Agent Note: ci-date-gate-timezone-and-flaky-test

Status: implemented

## Problem

两次推送后 CI（run `33329370935` 等）在 main 上持续红，核查为两个**预存在**问题（非依赖升级 `upgrade-ryn-and-dsh-runtime` 所致，先前提交 `9dcd4d3` 的 CI docs job 同样失败）：

1. **`verify-adr-format.py` 日期门禁有时区敏感的误报**：门禁用 `note_date > datetime.date.today()`（本机时区）判定"日期晚于今日"。GitHub runner 是 UTC（today=08-30），作者本地（UTC+8）已到 08-31，于是合法的 08-31 ADR 被误报"after today"而 FAIL。三个 08-31 ADR（含先前会话的 2 篇）因此挂红。
2. **`DesktopUpdateCommandRouterTests` 两用例 flaky**：`Check_PassesBackgroundToken_ToMachine` / `Check_WithoutBackgroundToken_NotCancellable` 用固定 `await Task.Delay(50)` 等 `RouteAsync` 里 fire-and-forget 的 `Task.Run` check 跑完；加载重的 runner 上 50ms 内后台任务未设置 `seen`，`Assert.NotNull(seen)` 偶发失败（CI 实测 `Nullable<CancellationToken>` 无值）。

## Decision

1. **date 门禁改 UTC + 1 天容差**：`note_date > datetime.now(timezone.utc).date() + timedelta(days=1)` 才 FAIL。时区差恒 ≤ 1 天（UTC+14 早 / UTC-12 晚），故该容差不误放真未来日期，同时容忍"作者本地已跨到次日"的合法情形。self-test 的 "future → fail" 用例改用相对 UTC 的 `today+2`（跨时区亦确定失败，避免 self-test 自身再依赖本机时区）。
2. **flaky 测试改确定性等待**：两用例去掉 `Task.Delay(50)`，改在 check 回调里置 `TaskCompletionSource`（`RunContinuationsAsynchronously`），测试 `await seenSignal.Task.WaitAsync(5s)` 后断言。无共享可变字段竞态、无固定延时。

## Alternatives considered

- **date 门禁改回 `date.today()` 但把 08-31 ADR 全部 re-date 到 08-30**：落败——ADR 首次提出日即作者本地日（08-31），re-date 是改写事实；且先前会话 2 篇已提交的 08-31 ADR 属他批范围，不随本批改。
- **date 门禁用 `date.today()` 去掉日期上限**：落败——会失去"拦截未来/明显错日"的机器防线。
- **flaky 测试用轮询 `while (seen is null) await Task.Delay(10)`**：可行但读写共享 `seen` 无可视化 happens-before；`TaskCompletionSource` 提供清晰的内存屏障，更干净。
- **重构 `RouteAsync` 让 check 被 RouteAsync await**：改变生产语义（check 本应 fire-and-forget，立即回当前态），非测试层可解决。落败。

## Consequences

- `verify-adr-format.py` 日期门禁对 UTC+/- 时区作者不再误报；真未来日期仍 FAIL。
- `DesktopUpdateCommandRouterTests` 3 用例稳定通过（本地 5/5 复跑 green），CI 该 flaky 消退。
- 验证：`verify-adr-format.py --self-test` 全过 + 实仓 69 篇 OK；`dotnet build` 0 警告、`dotnet test` 468/468；全部门禁 + `dotnet format` 绿。
- 待 main CI 复跑确认（run 新推后应 docs/build-test 双绿）。

## Related

- [upgrade-ryn-and-dsh-runtime](2026-08-31-upgrade-ryn-and-dsh-runtime.md)（implemented）：本 ADR 修复的是该批推送后暴露的预存在 CI 断裂，与被升级的 Ryn/dsh 版本本身无关。
