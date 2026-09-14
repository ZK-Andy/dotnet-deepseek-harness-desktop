# Agent Note: composition-root-value-flow-pipeline（组合根值流管线）

Status: proposed

## Problem

`DesktopBootstrap`（组合根）经两轮既有 ADR 治理后仍以 5 个 partial 平铺在主工程根、合计 1374 行，持有 25 个可变字段与 6 个空载荷阶段 token（字段数为立项估计口径；批次 0 依赖图实测 32 个——含 `_supervisorCtsRef` 非恒等别名 1 个、独立状态 31，正式口径批次 3 迁 implemented 时回写正文）：

- [split-program-main-god-function](../../implemented/architecture/2026-08-30-split-program-main-god-function.md)（implemented）：1337 行 `Program.Main` 拆为组合根 partial 类；
- [composition-root-stage-typing](../../implemented/architecture/2026-09-03-composition-root-stage-typing.md)（implemented）：`Run()` 顺序链推进为阶段 token 链（偏序类型流），但**字段归属迁移被显式推迟**（ADR 原话："下沉为参数即字段归属迁移，违反'不迁移字段归属'纪律"——"子域治理另立项"）。

由此残留三层问题：

1. **平铺本身**非病灶——dotnet/runtime 对大类型的官方惯例即 dot 后缀 partial 平铺于类型命名空间目录；单纯挪目录是观感解。
2. **分部落位失守**：哪个方法进哪个分部无机制约束，新方法塞哪个 partial 只取决于改动者；分部文件名承诺的分类学（App/Lifecycle/Navigation/Startup）已失守。
3. **根因 = 状态寄居组合根**：容器其实已在场（`RynApplication.ConfigureServices`），但形态是**倒置的半容器**——容器只持叶子（命令路由/托盘 UI），有状态编排（25 字段，口径同上实测 32）留在组合根，再经字段捕获闭包反向喂给容器（仅 `DesktopBootstrap.App.cs` 的 ConfigureServices 即 12 处 `new Xxx…` 装配、13 个组合根字段捕获点）。官方注入方向是"容器持类型、依赖构造注入"，现状恰为其反。

讨论确立判据：**解决 = 不变量从"人的纪律/注释"迁移进"机器"（编译器/测试/单点构造）；下放 = 不变量原样活着只换地址。**按此判据，token 链的偏序缺口（消费段整段漏调/乱序仍编译通过）之所以无法继续收紧，根因正是字段留在组合根：值无处产生、无处沉淀，只能用空 marker 顶替。本 ADR 即 09-03 显式推迟的"另立项"——字段迁移完成后，排序问题才真正交给类型系统。

边界查证（2026-09-15）：Ryn 无 Generic Host（`.cache/ryn-de` 全库零 `IHostedService`；`RynApplication` 为 `IAsyncDisposable` + `IServiceProvider` + 自管主线程 UI 事件循环，`RunAsync` 强制 thread 0）——**IHostedService 官方路径在本壳不可用**；`Run()` 即该 host 的启动编排，保留显式编排器是结构必然而非权宜。

## Proposal

四件套（同一重构的三个面 + 一个收口；批次明细、验证命令与恢复步骤见实施计划 `.plan/组合根值流管线-实施计划-2026-09-15.md`，本地工作文档）：

1. **状态下沉（注入化）**：B/C 类字段按子域迁入真类型服务——托盘行为（Hide/Recall/maximizedAtHide/trayReady）、更新栈（updateMachine/updateEnabled/readyNotified）、引导任务（Infrastructure 端口实现 + 壳侧导航收尾）、退出编排（单实例退出管道）；闭包喂容器改为构造注入。
2. **值流管线**：6 个空载荷 token 升格为**类型化阶段产出**——阶段返回真实值（如 `DshWebUrl` branded）、消费段收参数。排序不变量入编译器：缺前置产出即缺值编译失败；唯一填不动的是真并行 fan-out（健康/更新检查/横幅并发启动），而那本来无序，类型填不动它是正确的而非缺口。
3. **握手入类型**：引导完成 TCS（`_bootstrapSettled`）随引导服务私有化，对外暴露类型化完成句柄（消费方收参数，不再字段读）。
4. **退出幂等由构造**：单实例退出管道——有序步骤是构造数据 + 内嵌 once-guard，正常路径（托盘退出）与 `Run` finally 双路径共用同一实例；幂等由构造保证，记序 fake 可测。

### 已拍板决策（2026-09-15 讨论）

| 决策点 | 定案 |
|---|---|
| A 类启动配置字段（6 个：`_bootstrapOptions/_bootstrapGate/_preinstallGate/_isDev/_devAutoIsolated/_bootstrapNeeded`） | IOptions 式类型化配置单例，消费方构造注入所需不可变值 |
| 阶段值类型归属 | 跨 R3 边界的值 branded 进 Core；仅编排器内部流通的类型留组合根私有嵌套（沿 token 先例） |
| 分部文件终态 | 惰性：值流落地后若组合根仍 >400 行（R4 闸）则按官方 dot 后缀 partial 留至多 1 个分部；根上只留 `Program.cs` + `DesktopBootstrap.cs`（+至多一个分部） |
| 引导/退出新服务的端口接口命名与形状 | 实现期依赖图定稿，不预设 |

### 执行纪律（沿 2026-09-03 既有纪律）

1. **批次 0 先画依赖图**：穷举阶段/字段读写关系与闭包捕获点，产出拆分线与批次台账（同款纪律已实证）。
2. **四批次零行为变更**：0 记序安全网 → 1 状态下沉 → 2 值流管线 → 3 收口；每批 `dotnet test` 全绿（基线 614 起）+ 三重审核。
3. **批次边界即恢复点**：新会话可从任意未完成批次续跑，不要求一口气走完。

### 扩展性契约（三条，评审可依）

1. **值的寿命规则**：管线里流动的值寿命 = 单段（产出→相邻消费）；长命共享状态一律进服务——禁止借阶段返回值回填全局态。
2. **插入规则**：三个扩展方向均为加法——新阶段 = 阶段方法 + 新值类型；新后台服务 = fan-out 清单加一行；新子域 = 新服务 + DI 注册。
3. **门禁只强不弱**：重构使既有机器门禁变强——D005 主工程豁免面收缩（`new HttpClient` 等装配语句随下沉迁出）、A5 嵌套判据随值类型落地更新；architecture-standards 的组合根/命名空间描述随批次 3 同步。

## Alternatives considered

- **分部挪进 `Bootstrap/` 目录**：非官方解——dotnet/runtime 惯例即 dot 后缀 partial 平铺于类型命名空间目录；挪目录或付一次 namespace churn（目录即命名空间），或令目录与命名空间脱钩（违反官方惯例），收益仅观感。落败。
- **`BootstrapContext` record 分组字段**：composition-root-stage-typing 已否决（不消除耦合、加寻址层、顺序可读性反降）。本 ADR 的 A 类 IOptions 决策与之最近，须单独防重辩：否决对象是"把字段分组打包**在阶段间传 context**"；IOptions 单例是官方配置语义，消费方构造注入的是不可变值而非阶段间流动的 context。若实现期发现其实际退化为"换个名字的 context 参数流"，即触发重议（见 Risks）。
- **DI 容器全托管（编排器消失）**：composition-root-stage-typing 已否决，本轮复核后维持——Ryn 无 Generic Host（实证见 Problem），hosted-service 机器不存在，接手即手写编排器；前置段早退（单实例仲裁 return 0 静默退出）与条件注册（dev 门禁下不注册更新路由）无法进容器语义。落败。
- **IHostedService 接走后台段**：官方语义（StartAsync 注册序启动 / StopAsync 反序停止）本可干净映射后台三服务，但 RynApplication 不托管 hosted service，需自建驱动——即把编排器换个壳重写。落败；后台段现状 `Run()` 三行顺序启动 + finally/退出管道幂等收敛，在无 Generic Host 的宿主里已等价注册序语义。
- **只立"分部落位规则"不动类型**：不变量仍靠纪律（评审兜底），按本 ADR 判据属"下放"。落败——"哪个方法进哪个分部"的问题随真类型拆分被消解，而非被规则管理。
- **维持 token 链现状（偏序边界）**：消费段漏调/乱序无机器校验。落败——其"与并行 fan-out 结构一致"的部分被值流管线继承，非并行部分由值流填补；现状不可持续（分部持续增长、字段持续寄居）。

## Acceptance criteria

- 排序不变量编译期强制：不经前置阶段取值（如无 `StartRuntime` 产出而用 webUrl）、消费段漏调或乱序，在编译期缺值失败；组合根无空载荷 token。
- 握手：监督器/横幅任务对引导落定的等待经类型化完成句柄参数传递；`_bootstrapSettled` 字段消失。
- 退出：托盘退出与 `Run` finally 双路径共用单实例退出管道；幂等由 once-guard 构造保证；记序 fake 覆盖关键时序。
- 全程零行为变更：每批 `dotnet test` 全绿（基线 614 起）、0 警告、format 绿；三重审核逐批收口。
- 门禁面：D005 主工程豁免面收缩；A5 判据与 architecture-standards 描述同步更新。

## Risks

- **批次 1 风险最高**：后台 Task 闭包/事件委托/DI 回调的捕获点最多（App.cs 15 处闭包、Lifecycle.cs 全部后台任务）；缓解 = 批次 0 记序安全网先行 + 单批可回滚。
- **A 类 IOptions 边界**：与被否决的 BootstrapContext 语义最近（见 Alternatives）；实现期若发现退化为 context 参数流，触发重议而非强推。
- **值类型跨界归属**：branded 值进 Core 会扩大 Core 面——仅当值被 Core/Infrastructure 消费时才进，组合根私有的不进。
- **运行时敏感面不因重构而变**：GTK 主线程亲和、WebKitGTK 两跳 cookie（[bootstrap-cross-scheme-cookie-401](../../implemented/bug-fix/2026-09-14-bootstrap-cross-scheme-cookie-401.md)）、Close 事件丢失 8s 看门狗——零行为变更纪律以记序测试兜底。
- **语言下界**：C# 无法编译期证明 exactly-once 与异步竞态；归宿 = 记序 fake 与并发测试，属任何方案的共同下界，非本变更新欠账。

## Related

- [composition-root-stage-typing](../../implemented/architecture/2026-09-03-composition-root-stage-typing.md)（implemented）：token 链出处；本 ADR 完成其显式推迟的"字段归属迁移"半件并升格值流。
- [split-program-main-god-function](../../implemented/architecture/2026-08-30-split-program-main-god-function.md)（implemented）：组合根形态出处。
- [official-clean-architecture-adoption](../../implemented/architecture/2026-09-14-official-clean-architecture-adoption.md)（implemented）：三项目分层依据；本 ADR 的 Core 值类型归属受其 R2/R3 约束。
- 实施计划：`.plan/组合根值流管线-实施计划-2026-09-15.md`（本地工作文档，gitignore；批次明细、依赖图判据、验证命令与恢复步骤）。