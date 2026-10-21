# Agent Note: C7 装配期构造清单补静态工厂通道与死项核对

Status: implemented

Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

## Problem

`scripts/verify-compose-root.py` 的 C7 判据是「装配期构造清单」：`PreBuildRegistration*.cs` 里构造的对象必须登记在 `PREBUILD_CONSTRUCTIBLE`，否则报违规。它只认三个通道——`new Foo(...)`、DI 工厂 `ActivatorUtilities.CreateInstance<Foo>(...)`、target-typed `new()`。两处缺口：

1. **静态工厂构造不可见**：`PreBuildRegistration` 里 `CaptionBarOptions.Load(...)`、`LaunchOptions.Resolve(...)`、`RuntimeTimeouts.Load(...)` 都在装配期造对象，但源码里没有 `new` 关键字的落点，三通道全不命中。审计留下的是「补通道或写明例外」二选一；原件里 `CaptionBarOptions` 条目干脆带着「静态工厂形态属闸的盲区（待办区 C7 缺口①）」的自承，等于用注释代替判据。
2. **死项不报**：清单只查「构造了但不在册」，不查「在册但无构造点」。前者保证装配期构造都有代价可见，后者保证清单跟着代码走——一个对象被退休后清单条目会静默留下，下一个人读清单会误以为它还在装配面上（本批手删过一条 `CaptionBarOptions` 就是这样发现的）。

直接扫 `Type.Method(` 不可行：装配期代码里大量静态调用不是构造（原型实测命中 `Path.Combine`、`TimeSpan.FromSeconds`、`PagePump.PushUpdateState`、`UpdateBanner.ReadyScript`、`Tray.CloseGate`、`HarnessRuntimeHost.ResolveDshHome` 六个误报）。判据必须回答「这次静态调用的返回对象是否属于被跟踪的类型」。

## Decision

- 工厂通道：对受检源码里的静态方法先建「返回类型 → (接收者类型, 方法名)」映射——正则 `_STATIC_FACTORY_RE` 抓 `static [async] <返回类型> <方法名>(`，返回类型取末段并剥掉泛型参数，末段 ∈ 受跟踪类型集才登记为工厂；再以 `_STATIC_CALL_RE` 抓 `Type.Method(...)`，命中映射即计为一次装配期构造。判定与 `new`/DI 工厂/target-typed 同权：构造出的类型不在册即报；死项核对 `set(PREBUILD_CONSTRUCTIBLE) - set(构造点)` 报 `dead entry`——不按当前声明类型过滤：类型被删或改名留下的条目才是最典型的死项，自测以显式清单参数覆盖该形态。
- 清单以下游实际构造为准补齐两条：`LaunchOptions`（`LaunchOptions.Resolve(...)`，不可变配置值，ADR [composition-root-value-flow-pipeline](../architecture/2026-09-15-composition-root-value-flow-pipeline.md) 批次 3 的产物）与 `RuntimeTimeouts`（`RuntimeTimeouts.Load(...)`，唯一 Load 点，D4）；`CaptionBarOptions` 条目的「闸的盲区」自承删除——盲区已由本通道关闭。
- `_prebuild_violations` 拆成 `_prebuild_constructions`（返回违规行，清单可作参数传入），`_violations` 与自测共用同一实现；违规附源码行号，死项附清单行号（在 `PREBUILD_CONSTRUCTIBLE` 字面量内定位，同名字符串出现在别名清单时不会错位）；自测新增两条夹具：静态工厂未在册判红、类型删除后条目仍留（死项）判红。

## Alternatives considered

- **保留盲区、在清单条目上写明例外**：落败——那是把判据降级成注释；`LaunchOptions.Resolve` 这类入口会继续以「不在册也无人知道」的形态存在，而 C7 的意义正是让装配期的构造代价可见。
- **扫描所有 `Type.Method(` 静态调用**：落败——原型实测六个误报（见 Problem 段），误报会让闸失去可信度，最后被 `--enforce` 之外的方式旁路。
- **以 `var x = Type.Method(...)` 的局部变量声明为锚**：落败——`PreBuildRegistration` 里多数工厂调用的产物直接作为返回值或构造实参传出，没有落点变量；以返回类型为准才能覆盖两种形态。
- **改成 Roslyn analyzer（机器门优先）**：暂缓而非否决——analyzer 能拿到符号级的返回类型与接收者，不需要正则近似；但本闸是「清单 vs 源码」的仓库级一致性检查（还要读注册表常量、glob 文件面），搬进 analyzer 需要额外的项目图与注入面，当前的漂移代价用 Python 闸即可闭合。若同类漂移再现，再按 analyzer 路线重做。
- **要求所有装配期构造走注册表登记函数**：落败——那要求改生产代码的装配写法；闸应描述既有写法，而不是为了好检查去约束组合根的形状。

## Consequences

- 装配期的静态工厂构造现在与 `new` 同权：`CaptionBarOptions`/`LaunchOptions`/`RuntimeTimeouts` 三条都显式在册，新增未登记工厂构造会红。
- 死项核对让清单只减不增成为可执行约束：删代码忘了删条目会红，清单读起来与装配面一致。
- 近似面（仍可能漏）：工厂返回类型写成接口/基类、别名或写成 `object` 时不被登记；`static` 方法跨行或带特性/修饰符排列非常规时正则可能漏抓；受检文件面仍是 `Bootstrap/PreBuildRegistration*.cs`，装配构造若搬到别的文件不在本闸视域。这些是正则近似与文件 glob 的已知边界，门禁说明与文件头已登记。

## Testing

- `python3 scripts/verify-compose-root.py --self-test`：全绿（新增 `C7 static-factory construction flagged`、`C7 dead inventory entry flagged`）。
- `--enforce`：`root new-of-owned count: 3 (cap 6)`、`C7 inventory: 24 entries`、`compose-root: OK`（无死项违规）；变异实测——关掉工厂通道/死项核对各自夹具失配判红。

## Related

- [compose-root-form-separation](../architecture/2026-09-28-compose-root-form-separation.md)——C1–C6 判据出处；[value-flow-batch3-finalization](../architecture/2026-09-15-value-flow-batch3-finalization.md) 与 [d4-cleanup-batch](../architecture/2026-09-28-d4-cleanup-batch.md)——`LaunchOptions.Resolve`、`RuntimeTimeouts.Load` 的来源。
