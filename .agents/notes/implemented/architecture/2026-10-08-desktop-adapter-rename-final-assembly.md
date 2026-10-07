# Agent Note: 接线层诚实化与方向闸——改名 DesktopAdapter、终态装配、C5

Status: implemented

Review: FULL/2026-10-08/R1=ok R2=ok R3=ok

方案母篇见 [composition-mechanism-container-assembly](../../proposed/architecture/2026-10-08-composition-mechanism-container-assembly.md)（本篇落其 step-3"改名 + 闸改向"与递延的终态装配；step-1 见 [runtime-starter-core-usecase](2026-10-08-runtime-starter-core-usecase.md)，step-2a 见 [startupwiring-deletion-run-singletons](2026-10-08-startupwiring-deletion-run-singletons.md)，2b 见 [root-fields-zero-shared-state-singletons](2026-10-08-root-fields-zero-shared-state-singletons.md)）。

## Problem

2b 之后三处名实不符与机制残留：① `DesktopBootstrap` 之名宣称"组合根/启动"，实为无状态接线层（Ryn 拥有真正的根 `RynApplication.CreateBuilder().Build()`，真正的组合在 Core）；② `StartupSequence` 仍由根喂四个具体对象（2b 的容器回读只是换了只手递）；③ `IRuntimeStarter` 仍走缺省组装分支（容器里没它）；④ 闸只数体积（C3 ≤16），新功能加 `new Foo()` 两次以内不响——母篇要的方向断言（"接线层只引用端口 + Options + 适配器"）无机器载体。

另有一条旧红线须正面处理：[compose-root-form-separation](2026-09-28-compose-root-form-separation.md) Decision 曾定"`DesktopBootstrap` 类名不改（更名收益为 0 且断链）"——彼时（形态分离前）它确为组合根，编排住根内；四步走后根只剩触发，名字成了对归属的虚假陈述，收益不再为 0（新人阅读"Bootstrap"会去根里找编排），断链成本则可由本批全仓收口一次性结清。

## Decision

- 改名 `DesktopBootstrap` → `DesktopAdapter`（类 + `DesktopAdapter.cs`/`DesktopAdapter.App.cs`，`git mv` 保留沿革）："Bootstrap" 谎称根，"Wiring" 被已删定位器污染，"Host" 与宿主词汇撞车；"Adapter" 在本仓是诚实接线角色（前例 `UpdateStackAdapter`）。`Program` 调用点、注释/doc 引用、测试路径与扫描面、四门禁脚本（含各自 `--self-test` 夹具）、living 文档（AGENTS/三份 docs/cookbook 调试条/skill）、架构图三件套（ids 不动、只换 label）全仓收口；历史 ADR 与带日期 cookbook 条目冻结不动。
- 终态装配：`StartupSequence` 构造器删四个共享参数，改为从 `app.App.Services` 自解析（构造时点必晚于 Build，与 `ShowTray` 同型调用点解析）；`IRuntimeStarter` 以实例单例登记（`RegisterServices`，依赖全在手：`preflight.Bootstrap` + 超时 + 铸币），根组装经容器供给（可选注入 fake 的构造参数保留）。根组装行收至单行触发。
- 方向闸 C5：接线层构造的自有类型必须进 `ROOT_CONSTRUCTIBLE` 清单（16 项：触发本体 + 阶段值 + 容器单例 + 装配面构造 + 触发构造），新增构造类型即红（走 DI 或 deliberate + ADR）；C3 留数体积（16/16 顶帽——B1 评审发现限定名构造旁路：`new Tray.CloseGate()`/`new Bootstrap.StartupSequence(` 曾不可见，实数 16；提额否决，顶帽即棘轮按设计工作）。引用形参/局部仍属 R1 评审面（本批未动方法签名之外的引用形）。
- R1a 同修：`verify-registration-discipline.py` 的 `_NEW_RE` 同式限定名感知（其域外扫描实测零限定点，计数 1/1 与 11/15 不动——纯盲区消除，零行为变化）。
- R2S2 挂账澄清：`Func<…>` 不含圆括号，本就被元组豁免 `[^;{}]*` 容纳——补自测锁定（`MakeFn` 按名 flagged），无代码改动；真正的嵌套圆括号返回在树内零实例，不处理。
- 母篇处置：四步全落地，母篇 proposed 的使命完成——是否移目录/改写按 R3 归档纪律裁定（本篇不代裁）。

## Alternatives considered

- **`DesktopWiring`（落败）**："Wiring" 是两天前刚删除的反模式之名（`StartupWiring` 手搓定位器），重用必被误读为回潮。
- **`RynHostWiring`（落败）**：同上 + "Host" 与 `HarnessRuntimeHost`/`HostSetup`/`HostLog` 撞车；且更长。
- **不改名、只做装配与闸（落败）**：旧红线的"断链成本"论据仍在，但"收益为 0"已不成立（名字指错归属）；用户拍板按母篇全做，断链由本批收口结清。
- **引用全形断言一步到位（落败）**：对接线层全部具名引用（含形参/局部）做形状白名单，会把穿参残留（`BuildApp/RegisterServices` 的四个共享参数）一次性判红，逼出 struct 打包（2b 已判换皮）或更大重构；方向闸先钉构造维度（耦合的源头），引用维度留 R1 评审。
- **序列自解析改属性延迟求值（落败）**：构造期容器已就绪，一次性解析与延迟属性行为等价；属性增加调用点解析次数陈述面，无收益。

## Consequences

- 买到的：类名不再谎称归属；根不再向序列递任何共享对象（组装行 1 行）；`IRuntimeStarter` 缺省组装分支的生产路径被容器供给覆盖（fake 注入缝保留）；新功能在接线层加构造即红（C5），体积闸 C3 16/16 顶帽继续看量（任何新增构造即需 deliberate 提额）。
- 付出的：C3 13 → 16（`RegisterServices` 内 `new RuntimeStarter(` + B1 补数的两处限定名构造；顶帽未超，提额否决）；`ROOT_CONSTRUCTIBLE` 16 项祖父化清单需随 deliberate 变更维护；架构图三件套为 label 同步（非 archify 重生成，下次重生成覆盖）。
- 剩余：无（母篇四步全落地；母篇本文处置待 R3）。

## Testing

- `dotnet test` 全绿 0 警告：209 + 223 + 603 = 1035（计数不变，基线不动）。
- `CompositionRootSequenceTests` 六例全绿（路径与扫描面随改名同步；链序/值流锚未动——序列签名变更无文本锚依赖构造参数）。
- `dotnet format --verify-no-changes` 过；四闸 `--self-test` 过（含 C5、限定名归属与 `MakeFn` 用例）；`--enforce` 全绿（compose 16/16、C5 零行、registration 1/1 与 11/15、health OK、conventions OK、shell S1–S6 OK、skill OK）。
