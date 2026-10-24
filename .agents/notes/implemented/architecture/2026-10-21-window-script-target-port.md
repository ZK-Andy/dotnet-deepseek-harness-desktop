# Agent Note: 窗口脚本求值端口（两处失败面/顶栏用例可测化）

Status: implemented

Erratum: 2026-10-24 — 页面健康探针（PageHealthMonitor/Tracker/Recovery）与诊断 `health` 行已删除（ADR [drop-page-health-polling](../simplification/2026-10-24-drop-page-health-polling.md)）；本文相关提及为历史记录。

中文（双语暂不启用；启用时恢复 .md 与 .zh.md 镜像 + .i18n.yaml 配对机制）

Review: FULL/2026-10-21#2/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 2050，fanout ≤ 20（本批实测值见 `## Testing`；批次落地后 ADR 进入基线，豁免自动失效）。

## Problem

2026-10-20 的审计把 `RecoverySurface` 与 `CaptionBarInstaller` 列为「编排层有实现但无自动化测试」，当时的结论是两处**不可测**——两者的卡点不同，须分开记：

- `RecoverySurface` 直收 `CurrentWindowAccessor`（无公开构造）、`Func<IRynWindow>`（接口 40 余成员、仓内无假件）与 `TrayController`；
- `CaptionBarInstaller` 只卡在 `CurrentWindowAccessor` 这一项（其 webview 面是 `IRynWebView` 接口，仓内已有假件）。

同批还留下一处不对称：`PagePump` 的四路注入里，webview 注册路（`Func<IRynWebView>`）有测试，窗口求值路（`accessor.Current.EvaluateJavaScriptAsync`）没有——因为后者同样被 `CurrentWindowAccessor` 的构造性卡住。

仓内还有第三个同源的求值点：`PageHealthMonitor` 的页面健康探针也直接调 `accessor.Current.EvaluateJavaScriptAsync`，并自行 `Trim('"')` 剥引号解壳（与 `RynProbeValue.Decode` 形成 raw/decoded 两套契约）。它同样是「对当前窗口求值一段 JS」，只是没人为它开缝。

这几处用例承载的是壳的行为骨架：失败面「先批准闸门再关窗、破坏性动作只挂显式 true、通道故障只留日志」，顶栏「三路注入 + 导航后重注入 + 注册一次性闩」。改坏了只会由人眼在实机上发现。缺的缝是一件事：**对当前窗口求值一段 JS**——这些用例的窗口动作就只有这一条（关窗已由 `RecoveryCommandRouter` 的「闸门 + 动作」形态覆盖先例）。

## Decision

抽出 `IWindowScriptTarget`（Presentation 层 `PageBridge/`）：`Task<string> EvaluateAsync(string script, CancellationToken ct)`；未就绪统一抛 `InvalidOperationException`（Ryn 的 `Current` 本就以此型表达），故 `PagePump` 的「未就绪按节拍重试」单点语义零变化。Ryn 适配器 `WindowScriptTarget` 只包一个惰性 `Func<CurrentWindowAccessor?>`（provider 为 null 同样归一成 `InvalidOperationException`），并把 `CancellationToken` 透传到 Ryn 的 `EvaluateJavaScriptAsync`——本仓**唯一**接触 `CurrentWindowAccessor` 求值面的适配器，R3 边界仍留在 Presentation。

求值面与解壳面各收口到一处：`IWebViewNavigator.EvaluateScriptAsync` 原先自己调 `accessor.Current.EvaluateJavaScriptAsync` 再解码（raw 形态的桥回报在实现内解完，未出实现侧），现改为经同一端口求值并解码（`Decode(await port…)`）；页面健康探针（`PageHealthMonitor`）原先亦直调访问器并自行剥引号，现同样经端口求值、经 `RynProbeValue.Decode` 解壳（其 `Parse` 判读点随之只收已解码观测值，带引号形态判 Unknown）。故 Ryn 求值适配器与回报解码各只有一处，raw/decoded 两套契约不再并存。

`PageHealthMonitor` 因此收为 `internal`（引用面全在本程序集内）：public 类配 internal 端口形参会触发 CS0051（不一致可访问性），且该类型在本仓没有外部消费者。

装配保持单一实例：`PreBuildRegistration.CreateSharedState(accessorProvider)` 造好端口，`SharedState.ScriptTarget` 携带，`AddBootstrapSharedState` 登记同一实例；装配期消费方（`FirstBootUi`、自更新横幅/状态推送）与容器期消费方（`StartupNoticeService` 闭包、`CaptionBarInstaller`、`RecoverySurface`、`PageHealthMonitor`、导航端口）拿的是同一个对象。`PagePump` 的四个注入方法由 `CurrentWindowAccessor` 参数改收端口。

`RecoverySurface` 的窗口面收成两个端口：页内注入走 `IWindowScriptTarget`，退出走 `CloseGate` + `Action closeWindow`（与 `RecoveryCommandRouter` 同构），Ryn 类型全部退出该类。`CaptionBarInstaller` 只换收参类型，三路注入、`_reregistered` 闩、`MacNativeChrome` 判据与脚本构建不动；其 `Install` 改为 `InstallAsync` 并返回「当窗注入 + 每页加载注册」两路的聚合任务（生产调用点仍按 fire-and-forget 消费，测试据此确定性等待，取消语义不再是定时窗口内的观察）。

覆盖（新增 20 例）：

- `RecoverySurfaceTests`（8）：原生通道投影 / 「退出」先批准再关窗且只关一次（顺序契约取「关窗瞬间」的闸门态取证，不是事后看最终态）/ false 不触发破坏性动作 / 诊断导出失败走失败文案 / 英文分支 / 关窗抛异常只留日志 / 模态通道故障时闸门与窗口都不动 / 页内面脚本走构建器。
- `CaptionBarInstallerTests`（4）：`InstallAsync` 两路同发且脚本同源 / 导航到达重注入并补注册一次 / 第二次导航不再新增注册 / 令牌已取消两路都不发（await 完聚合任务后断言，结论确定）。
- `PagePumpTests`（+4）：端口 `InvalidOperationException` 重试至成功、其余异常一次性放弃、自更新状态推送派发页内事件、窗口未就绪时推送静默丢弃。
- `WindowScriptTargetTests`（1）：访问器提供者缺位时归一化为「未就绪」型异常。
- `PageHealthMonitorTests`（+3）：探针回报解壳后判 Alive（真实回包形态 JSON 字符串字面量）、探针抛异常记 Unknown 且续跑、取消令牌透传到端口。
- `HarnessRuntimeHost.Handoff` 不新增：交接面已有 7 个 `TryRideMarketRelayAsync_*` 用例（收养/无证据/令牌不符/取消等），2026-10-20 那篇已写明「本批不新增」，故不再复述归属。

## Alternatives considered

- **造 `IRynWindow` 假件**：落败——40 余成员的手写假件本身即成大块无业务含义的测试代码，且会随 Ryn 版本漂移失真；本仓只需要「求值脚本」一个动作。
- **反射/`InternalsVisibleTo` 直造 `CurrentWindowAccessor`**：落败——依赖 Ryn 内部构造签名，升级即碎；且测试会绕过真实接线，证明不了装配正确。
- **只给 `RecoverySurface` 开缝、顶栏维持不动**：落败——顶栏的当窗注入与恢复面是同一个窗口动作，开两条缝会让「怎么碰窗口」出现第二个答案。
- **把 `PageHealthMonitor` 一并端口化**：采纳——它的探针正是脚本求值；留着第二个求值点等于让「怎么对当前窗口求值」有两个答案，且 raw/decoded 两套契约并存。改造后探针路首次进入自动化覆盖。
- **把 `TrayController` 也端口化**：落败——它要的是原生窗口操作（hide/maximize/Closing 订阅），不是脚本求值；为它扩出第二个端口属过度设计，留待出现真实驱动需求时再定。
- **端口不收 `CancellationToken`**：落败——Ryn 的 `EvaluateJavaScriptAsync` 有 `CancellationToken` 形参，端口吞掉它就把取消语义截在边界外（与 Ryn 侧行为不一致）。按设计无取消位的面（Core 导航端口的 `EvaluateScriptAsync`、恢复面 `ShowPageAsync`、静态推送面 `PushUpdateState`）保持既有形状，取消由调用方有界等待承担。
- **`PageHealthMonitor` 留 public**：落败——public 类配 internal 端口形参触发 CS0051；其引用面全在本程序集内，收为 internal 不影响任何消费者。

## Consequences

- 收益：两处编排用例进入自动化回归；`PagePump` 的窗口求值路与注册路现在对称可测；失败面的顺序契约（批准 → 关窗）由用例钉住。`RecoverySurface` 不再持有任何 Ryn 类型，关窗动作与恢复页命令路由共用同一形态；Ryn 求值面与解壳面各收口为单一适配器，raw/decoded 两套契约不再并存；页面健康探针路有了自动化覆盖。
- 代价：`SharedState` 多一个成员；装配面跟值 20 处（方法级口径：一个方法计一次，方法体内多处跟值不叠加）——签名变更 10 处（`FirstBootUi` ctor、`WebViewNavigator` ctor、`PageHealthMonitor` ctor、`CaptionBarInstaller` ctor、`RecoverySurface` ctor、`PagePump` 的四个注入/推送方法、`CaptionBarInstaller.Install`→`InstallAsync` 改名并返回两路聚合）、共享态与装配 4 处（`SharedState` 加成员、`CreateSharedState` 造端口、`CreatePreflight` 消费端口、`CreateUpdateAndTrayAssembly`——其函数体内两处跟值按一个方法计）、组合根 1 处（`DesktopAdapter` 造访问器提供者）、容器注册 4 处（`AddBootstrapSharedState` 登记端口实例、`AddHealthMonitor` 注入端口、`AddStartupUseCases` 的导航端口/恢复面/顶栏三处登记改走端口、`AddStartupNotice` 的横幅推送闭包改走端口）、调用点 1 处（`StartupSequence.Supervision` 改调 `InstallAsync`）。窗口求值多一层端口间接（无行为变化，`PagePump` 重试语义不变）。
- 边界：`TrayController` 仍收 `Func<IRynWindow>` + `Func<CurrentWindowAccessor?>`（非访问器本身）——它要的是原生窗口操作而非脚本求值，不在本端口语义内，改动需各自 ADR。
- 门禁：`verify-compose-root` 的 C7 装配期清单新增 `WindowScriptTarget`（装配期 `new` 的登记条目，理由内联于清单）。
- 增量闸：本批超 gross 帽，按 `scripts/verify-increment-discipline.py --exempt` 一次性豁免；钩子与 CI 的 `--exempt` 路径随之指向本篇，批次落地后自动失效。

## Testing

- `dotnet build dotnet-deepseek-harness-desktop.slnx -c Debug`：0 警告；`dotnet test` 全绿，用例总数由 `scripts/test-baseline.json` 单一来源持有（本篇不复制）。
- 门禁：`verify-compose-root.py --enforce`、`verify-code-health.py`、`verify-code-conventions.py`、`verify-registration-discipline.py`、`verify-increment-discipline.py --exempt <本篇>` 全绿。
- 增量闸实测（本批变更集对基线）：gross 1984 行（帽 2050）、fanout 1（帽 20）；豁免值按实测上取整，批次落地后自动失效。

## Related

- [review 前的窗口访问层现状](../testing/2026-10-20-orchestration-test-coverage.md)：本篇落地其「前置是 Ryn 窗口面端口化」的条件。
- [原生失败面](../architecture/2026-10-14-native-failure-surface.md)：失败面的通道语义与退出极性。
- [恢复页命令路由](../../../../src/DeepSeek.Harness.Desktop/Recovery/RecoveryCommandRouter.cs)：本类关闭动作的同构先例。
