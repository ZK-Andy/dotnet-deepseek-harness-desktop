# Agent Note: composition-root-field-cleanup（组合根字段收口——别名折叠与死信号删除）

Status: implemented

## Problem

组合根 `DesktopBootstrap`（4 个 partial，ADR `split-program-main-god-function`）在拆分后留下三处"名不副实"的字段残渣，均以 `TODO(...)` 标注：

1. **`_updateWindow` = `_windowAccessor` 恒等别名**（TODO `window-accessor-alias`）：BuildApp 末 `_updateWindow = _windowAccessor` 赋值一次、永不分化，全部使用等价。留着只增加一个需跨 partial 追踪的冗余字段名。
2. **`_startupNavigationSettled` 只写不读**（TODO `navigation-settled-unconsumed`）：被 `RynNavigationCallbacks.SetOnNavigated` 写入（`TrySetResult`）、无任何消费者。其源头是 ADR `shell-firstboot-hardening`（v0.3.0 切换日横幅竞速 bug）的"横幅导航门控"，但该机制已被 `PagePump.ShowBannerWhenReadyAsync` 的 30×1s 自重复试环取代——横幅无需外部"等导航落地"信号（重试环自身幂等），崩溃恢复走 `showRecovery` 直注入也不依赖它。字段与接线残留却让读者误以为门控仍活（ADR `ryn-navigation-callbacks` 称其"给横幅门控提供到达信号"，现实无消费者）。
3. **`_supervisorCtsRef` = `_supervisorCts` 第二引用**：非别名，是"命令路由（RegisterServices）注册时点早于 SetupSupervisor 给 `_supervisorCts` 赋值"的延迟捕获模式（`DesktopUpdateCommandRouter` 的 `backgroundToken` 闭包惰性读它）。无注释时易被误当 `_updateWindow` 同类别名而误删。

## Decision

1. **fold `_updateWindow` → `_windowAccessor`**：删 `_updateWindow` 字段与 TODO、删 BuildApp 末的别名赋值，三处使用点（激活回调 / 更新安装后关窗 / PushUpdateState）改读 `_windowAccessor`。**保留原 null 语义**：`_windowAccessor` 声明为 `CurrentWindowAccessor`（`null!` 占位），运行时在 BuildApp 前即为 null——激活回调 `CurrentWindowAccessor? accessor = _windowAccessor; if (accessor is null) return;` 在"窗口建好前到达的激活请求"上与原 `_updateWindow`（可空，build 前 null）行为完全一致（静默忽略，不记日志）。
2. **cut `_startupNavigationSettled`**：删字段 + TODO、删 SetupSupervisor 中的 TCS 初始化与 `SetOnNavigated(() => _startupNavigationSettled.TrySetResult())` 接线。`RynNavigationCallbacks.SetOnNavigated` 是 public API + 有测试（`RynNavigationCallbacksTests`），**保留方法**，仅同步其 XML 注释为"当前无宿主消费者，作可扩展能力保留"。cut 后 `OnWebViewNavigated` 内部 `_onNavigatedImpl` 恒 null → invoke no-op，无副作用。
3. **`_supervisorCtsRef` 保留 + 补注释**：字段声明处补"延迟捕获、非别名、勿删"注释，防止将来被误当恒等别名清理。

## Alternatives considered

- **`_updateWindow` 不做 fold，保留原状**：字段冗余 + TODO 长留，与"拆分后应收口"的治理方向相悖。落败。
- **fold 时把激活回调改为"尝试 Show + 记失败日志"**（利用 `Current` 未就绪抛 `InvalidOperationException`）：能顺带让"窗口建好前到达的激活请求"在窗口就绪后重试——但这是行为变更（日志面 + 显示语义），违反"零行为变更"纪律，且该窗口期极罕见。落败——保持静默忽略原语义。
- **把 `_windowAccessor` 改声明为可空以精确表达"BuildApp 前未就绪"**：波及 PagePump/PageHealthMonitor 等大量非空消费点（需逐个 `!` 或判空），改动面失控、超出收口范围。落败——`null!` 占位 + 注释已足够，激活回调经可空局部保持判断。
- **cut `_startupNavigationSettled` 时连同 `SetOnNavigated`/`_onNavigatedImpl` 一起删**：该方法有测试覆盖、是 Ryn.Callbacks 能力的对外扩展点，删除缩小 public API 面、无必要。落败——保留方法 + 注释，仅删组合根接线。
- **保留 `_startupNavigationSettled` 但"真接回横幅门控"（wire）**：需先回答"为什么 `ShowBannerWhenReadyAsync` 的 30×1s 重试环不够"——目前无答案（重试环已覆盖窗口未就绪与导航替换场景）。落败——wire 无真实需求支撑。

## Consequences

- `DesktopBootstrap` 字段净减 2（`_updateWindow`/`_startupNavigationSettled`），TODO 净减 2；`_supervisorCtsRef` 获成因注释。
- 行为零变更：语句序/分支/null 语义/日志语料逐一对应（激活回调静默忽略语义保留、TCS 无消费者、`SetOnNavigated` 保留但无调用方）。
- `RynNavigationCallbacks` 的类级与方法级 XML 注释同步为现实（无宿主消费者、能力保留），消除"给横幅门控提供信号"的过时表述（D004）。
- 测试与门禁：`dotnet test` 449/449、build 0 警告、`dotnet format` 绿、code-health/conventions 绿。

### Testing

- build 0 警告 0 错误；`dotnet test` 449/449 通过。
- `dotnet format --verify-no-changes` 绿（无风格回归）。
- `verify-code-health.py --enforce` / `verify-code-conventions.py --enforce` 绿。
- 三重审核判据：纯结构重构零行为变更（无 env 注入/无公共签名变更/无 spawn 形态/无可观察副作用/无 async·并发语义变化），走轻审（R2 代码面）。

## Related

- [split-program-main-god-function](../architecture/2026-08-30-split-program-main-god-function.md)（implemented）：`Program.Main` → `DesktopBootstrap` 拆分，本收口处理其遗留的字段残渣。
- [ryn-navigation-callbacks](../feature/2026-08-28-ryn-navigation-callbacks.md)（implemented）：`_startupNavigationSettled` 横幅门控的出处；本收口判定其机制已死、接线残留，cut 并同步注释。
- [shell-firstboot-hardening](../bug-fix/2026-08-24-shell-firstboot-hardening.md)（implemented）：横幅门控要防的实机 bug；cut 前确认 `ShowBannerWhenReadyAsync` 自重复试环已覆盖其场景。
