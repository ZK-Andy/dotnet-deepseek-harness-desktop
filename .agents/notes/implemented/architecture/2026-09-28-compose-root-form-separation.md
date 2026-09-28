# Agent Note: 组合根形态分离——注册下沉与编排搬出根

Status: implemented

Review: FULL/2026-09-28#7/R1=ok R2=ok R3=ok

各路 2 轮：R1/R2 首轮各报 1 Blocker（同一处——RuntimeTimeouts.Load 搬迁丢失，回填收口）；R3 首轮报 4 Blocker（supersession 义务：batch3 终态矛盾前向指针 + 三处落点换家 Erratum），验轮全闭。

## Problem

组合根的「注册」与「编排」从未分离：容器（Ryn builder 即 MEDI）只装 Ryn 叶子，自有服务全部手 `new`，启动编排写成根上的字段 + 阶段方法链。F3 的 400 行是**每文件**预算，增长必然转化为分部增殖——12 天内 `729 行/2 文件 → 1074 行/7 文件`（+47%）而门禁全绿（实测 2026-09-27，基线 `5861904`）。一次真收缩（`1434271`）后 12 天又复发，因为形态没变：搬出去的代价（FULL 档 + ADR + 重写源文本安全网）高于加一个分部（只需 `git add`），而唯一盯「新功能塞组合根」的机器门 A5 已退役，四个新分部（WindowReady/WebAuth/WebkitSandbox/Navigation）注册行与 `new` 全为 0，纯属编排逻辑借 F3 每文件预算增殖。另一条自证链条：根上 16 处 `services.Add` 只占组合根 7.8% 行数且集中在 App 分部一处，其余 92.2% 是编排/策略/平台/UI/兜底——「服务变多所以根变长」不成立，变长的是编排。

## Decision

组合根拆成两件事，一次做到位（ADR composition-root-value-flow-pipeline 的值流管线在此之上的形态升级）：

1. **注册下沉为按域 `AddXxx()` 扩展**（与域共址，不占根预算）：`BootstrapRegistration.AddBootstrapCommands`、`TrayRegistration.AddTrayServices`、`UpdateRegistration.AddUpdateCommands`、`RecoveryRegistration.AddDiagnosticsCommands/AddRecoveryCommands`、`ShellCommandRegistration.AddExternalLinkRouting/AddLocaleCommands/AddAutostartCommands`；Ryn 源生成扩展（`AddRynCommands/AddRynCallbacks/AddRynNavigationCallbacks`）与工厂闭包的组装仍由组合根 `RegisterServices` 统一调用。扩展留在 shell 工程按域分文件，不给 Infrastructure/Core 加 `Microsoft.Extensions.DependencyInjection.Abstractions`。
2. **启动编排搬出根，成为容器解析的普通服务**：Core 新端口 `Core.Bootstrap.IStartupSequence`（`int Run()`），实现在 shell `Bootstrap/` 域（`StartupSequence` + Supervision/Navigation/WebSession 三个分部，承接原组合根全部阶段方法与 Navigation/WebAuth/WindowReady/WebkitSandbox 四个分部）。组合根退化为：沙箱降级 → 运行时与 dev 解析 → 单实例仲裁 → 代理启动 → 关窗闸门/更新栈/托盘装配 → `Build()` → `GetRequiredService<IStartupSequence>().Run()` → 代理释放。编排器必须存在且由根显式触发（Ryn 无 Generic Host，`RunAsync` 强制 thread 0，容器不能充当启动驱动）——本决定终结的是「编排器住在组合根」，不是「编排器由容器驱动」。
3. **阶段产物升正式类型**：`Preflight/HostSetup/UpdateSetup/AppSetup/SupervisorSetup` 落 `Bootstrap/StartupStages.cs`（internal record struct）。原 `RuntimeSetup` 就势退役——搬迁后 `WebUrl` 的两个消费点（`InitCloseGateAndUpdateStack` 的纯序注释形参、`BuildApp` 的日志 `dsh=就绪/未起`）均失去消费者，按值流验收（`StageOutputs_CarryConsumedValues` 拦死载荷）不设无消费产出；dsh web URL 的下游（监督器/收养导航）各自从宿主或回调取值，现状即此。
4. **惰性接线槽随编排迁移**：组合根「注册早于赋值」的捕获字段（`_app/_windowAccessor/_host/_supervisorCtsRef/_tray/_exit/_healthMonitor/_lastRecoveryShownAtUtc`）落到 `StartupWiring`（internal，仅启动编排期存活）；注册闭包捕获该实例，槽位由后到期的阶段回填，语义与搬迁前逐一等价。`_webUrl` 纯冗余别名（三处写全部等于 `_proxy?.Url`，唯一读点为健康 reload）就势删除，reload 委托直读 `_proxy`。
5. **代理产出结果对象**（值流真空修复）：`StartProxy()` 从「无参无返回、写字段」改为 `DshLoopbackProxy.TryCreate(...)` 返回 `ProxySetup(Proxy, Cts)`，消费段收参；绑定异常类型清单（协议/平台策略）随迁 Infrastructure。`ProxySetup.Dispose` 收口组合根尾部的释放序（cancel → dispose cts → dispose proxy）。
6. **安全网同步重写**：`CompositionRootSequenceTests` 的源文本锚从 `DesktopBootstrap.cs` 改为双链断言——组合根容器前头部 7 段（`DesktopBootstrap.cs`）+ 编排主链 11 段（`StartupSequence.cs`）；产出消费点扫描面扩到 `StartupSequence*.cs` 分部。另补 `TryCreate_BindsAndDisposeCancelsToken` 回归。

**等价排列（两处顺序变化，构造无交互实证）**：托盘/更新栈装配（原阶段 8，在 spawn 之后）移至 Build 之前；代理启动（原阶段 9）与装配段对调。二者构造只收惰性委托与配置，与 dsh/窗口无交互；窗口创建仍在 `app.Run()`（编排 RunAppLoop 内），晚于 spawn。`BuildApp` 的 opts 日志随之不再含 `dsh=就绪/未起` 尾巴（该状态由编排 `StartRuntime` 的既有日志承载）。

**红线保持**：单实例早退留在容器之前；WebKit 沙箱降级留组合根入口首行（`WebkitSandboxFallback.Apply`）；`DesktopBootstrap` 类名不改（`ShellEntry` 更名收益为 0 且断链 review-tier/ADR/测试锚）。

## Alternatives considered

- **只搬逻辑不换形态**（保留根上阶段方法链，靠收紧的总量闸守）：即第三次重复 `1434271`——一次真收缩后 12 天又 +47%，因为形态没变、A5 已退役、搬出去的代价高于加分部。落败。
- **编排器搬进容器由容器驱动启动（Generic Host / IHostedService）**：容器在启动链中途才诞生（`.Build()` 此前约 250 行手装配已完成），不能充当启动驱动；Ryn 无 hosted-service 机制。ADR composition-root-value-flow-pipeline 已两次否决「编排器消失」这一极端，本决定保留显式触发。落败。
- **给 Infrastructure/Core 加 `Microsoft.Extensions.DependencyInjection.Abstractions` 让注册扩展与域真正共址**：改变「Core/Infrastructure 不碰 DI 容器」的既有姿态（R2 管的是 ProjectReference，该包是框架抽象，不违 R2），收益是扩展离根更近。落败（暂）：先不加，待形态稳定后作独立项评估。
- **组合根更名 `ShellEntry`**：方案文本的目标形态含更名；实际收益为 0（纯改名），且断链 `verify-review-tier.py` 的 compose-root 触发面（文件名前缀）、ADR 交叉引用与测试路径锚。落败。
- **注册扩展塞进单个 god 文件**：违背「按域分文件」目标，重复注册面撞车史。落败。

## Consequences

- 组合根文件集合计 1074+43 → 309 行（`Program.cs` 43 + `DesktopBootstrap.cs` 127 + `DesktopBootstrap.App.cs` 139），回落到两个 .cs 文件 + Program；新增编排/注册/类型 939 行落 `Bootstrap/` 等域内。F3 的「每文件 400」判据就此失效（309 行总量的根仍有增殖空间）——D2 换闸（集合计 ≤500 + `verify-compose-root.py` 语义白名单）是本决定的配套，未落地前根的语义面暂由评审兜底。
- §4.1 搬家表 #1–#3/#6–#10（profile 生命周期、companion 安装、共享 home 横幅、WebAuth 用例、诊断导出、`PathsEqual`、恢复原因码等细搬）保留在编排服务内，未再向 Core 端口/Infrastructure 细化——形态分离已使这些细搬落 `src/**` 非根文件名 + `tests/**`，不再命中 FULL 面，可作 LIGHT 批次跟进。#5（单实例 socket 拼装）属容器前单实例仲裁面，按本 ADR 红线留组合根。
- 覆盖率 59.95% → 59.71%（-0.24pp，n=1 本地并集复算）：下降全部来自新增装配胶水（注册扩展/接线槽/结果对象包装）无测试触达；编排主体是整块搬迁，触达面不变。组合根及其编排历史上即不可在单测执行，此差额按基线跟值批口径入账（`854→856` 含序列安全网拆分 +1 与代理 `TryCreate` 回归 +1）。
- 组合根的 `DesktopBootstrap*` 文件名触发性不变：后续触碰根文件仍是 FULL 档；编排/注册文件（`Bootstrap/StartupSequence*`、`*Registration.cs`）不在触发面，属 LIGHT。
- 挂账已清（2026-09-29 归档批，ADR post-restructure-ledger-batch）：[2026-09-14-nav-partial-import-order-fix](../../archived/process/2026-09-14-nav-partial-import-order-fix.md) 的主题文件（`DesktopBootstrap.Navigation.cs`）已随本批删除，一次性 process 历史无指引价值，符合归档判据。

## Testing

- `dotnet test` 856/856 全绿（Core 166 / Presentation 183 / Infrastructure 507）；`dotnet build` 0 警告；`dotnet format --verify-no-changes` 0 差异；`verify-code-health.py --enforce`、`verify-code-conventions.py --enforce` 绿；覆盖率并集复算 59.71%（本地，n=1）。
- 行为等价依据：语句序/分支/异常边界逐一对应的纯搬迁（见 Decision），加上述两处等价排列的构造无交互论证；三平台冒烟归 CI dispatch 验证（本批不涉打包/workflow 面）。

## Related

- 总纲：`.plan/整改方案-三平台震荡后结构清理-2026-09-27.md` §0.5.1/§4.1（待审方案，本批为其 D1；D2 换闸、D3 端口补齐、D4 值流/去重/死代码各带自己的 ADR）。
- 前序：[composition-root-value-flow-pipeline](2026-09-15-value-flow-batch2-value-pipeline.md)（阶段产出值流，本批把其私有嵌套升正式类型并搬出根）及其批次 3 [value-flow-batch3-finalization](2026-09-15-value-flow-batch3-finalization.md)（其 Decision 2「分部文件终态」由本批推翻，已互挂 Superseded 行）。
- 规范：[architecture-standards.md](../../../../docs/architecture-standards.md) R1。
