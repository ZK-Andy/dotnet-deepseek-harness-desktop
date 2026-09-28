# Agent Note: 自更新协调器迁 Core——更新栈四端口与配套单测

Status: implemented

Review: FULL/2026-09-28#9/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`UpdateCoordinator`（185 行）住在 Presentation 且直连 9 个 Infrastructure 类型/成员（`UpdateOptions`/`UpdateHttpClient`/`HarnessRuntimeHost.ResolveDshHome`/`UpdatePlatform`/`StalePackagePruner`/`ReleaseMetaClient`/`InstallerDownloader`/`UpdateInstaller`/`FileReadyPersistence`）——自更新编排是 Application 用例，却与 UI 桥（`PagePump`/`UpdateBanner`/`CloseGate`）同层混居；`tests/.../Update/` 只有两个 Router/Banner 测试，协调器的编排策略（dev 门禁、对账清扫时机、装前 SHA 复取序、ready 横幅去重、后台检查异常收口）零回归网。整改方案（`.plan/整改方案-三平台震荡后结构清理-2026-09-27.md` §4.1 表 #12、§4.2「更新栈」行）裁定：迁 Core 用例 + Infrastructure 适配器，配套单测；`UpdateHttpClient` 归位随批。

## Decision

1. **Core 新端口四个**（`DeepSeek.Harness.Desktop.Core.Update`，按 §4.2 MVP 判据小而专注，`UpdateStateMachine.IPersistence` 沿用既有）：
   - `IUpdateEnvironment`：宿主事实与对账（`IsEnabled`/`LoadOptions`/`CurrentVersion`/`ResolveUpdatesDir`/`UpdateRid`/`DetectPackageKind`/`PruneStale`/`CreateReadyPersistence`；`CurrentVersion` 随端口注入——`AppVersion.Current()` 读入口程序集，测试宿主下串版本）。
   - `IReleaseFeed`：feed 抓取（`FetchLatestAsync(options, rid, pkgKind, ct)`）。
   - `IPackageDownloader`：下载 + 装前 SHA 复取（`DownloadAsync`/`FetchExpectedSha256Async`）。
   - `IPackageInstaller`：安装器拉起（`LaunchAsync`）。
2. **`UpdateOptions` 迁 Core（纯模型）**：record + `IsEnabledFor` + `Parse`（internal，`InternalsVisibleTo` 覆盖 Infrastructure 与测试工程）；**文件装载（appsettings 读盘）下沉适配器**——Core 零白名单纪律不破（D005），配置模型进 Core、边界 IO 留 Infrastructure。
3. **`UpdateCoordinator` 整体迁 Core**：构造收 `bool isDev` + 四端口 + 六个 UI 交接委托（supervisor 令牌、状态推送、就绪横幅、退出批准、关窗、兜底强退）+ 日志回调；语句序/分支/异常边界零变更。dev 门禁的环境变量读取随适配器落 Infrastructure（边界读环境，判定 `IsEnabledFor` 纯函数不变）；RID/包类型/当前版本在 `Load` 一次捕获（进程不变事实，R1 建议采纳）。
4. **Infrastructure 新适配器 `UpdateStackAdapter`**：同型实现四端口（一个具体类型），持有懒构造的共享 `HttpClient`（`UpdateHttpClient.Create` 收口于此——归位随批达成：Presentation 层不再有栈类型引用）；`UpdateRid()` 平台判定随迁。
5. **组合根装配面改写**（`DesktopBootstrap.App.cs` `InitCloseGateAndUpdateStack`）：构造适配器 + 协调器（四端口同传适配器实例、UI 交接以委托闭包接线 `PagePump`/`UpdateBanner`/`CloseGate`/`StartupWiring`），`Load()` 时点不变（早于 BuildApp 的命令路由注册）。`StartupStages.UpdateSetup` 类型引用随命名空间改。
6. **配套单测**（`Core.Tests/Update/UpdateCoordinatorTests.cs`，全假件，七条）：dev 门禁不装载、装载即对账清扫+留痕、检查链路（feed 收 RID/包类型、下载收目录与配置超时）、安装路径事件序（SHA 复取→拉起→批准→关窗→兜底强退）、拉起失败回退 ready 且不触发 UI 交接、ready 横幅去重（失败回 ready 再触发仍一次）、后台检查持久化异常落日志。

## Alternatives considered

- **端口收成单个 `IUpdateStack` 门面（12 成员）**：协调器构造参数更少，但环境事实/feed/下载/安装四种关注点挤一张接口，任一消费方扩面都动全接口；§4.2 明言「小而专注的 3–4 个，不是 7 个」。落败。
- **UI 交接委托打包成 Core record**（`UpdateUiCallbacks`）：构造参数 12→7，但多一个自有类型进组合根 `new` 计数（15→17 撞 verify-compose-root C3 上限 16），且六个委托本就是一次性接线、无复用面。落败。
- **只抽端口不迁类**（协调器留 Presentation）：接口在 Core、消费方在外层，方向反了；§4.1 表 #12 明确「Core 用例 + Infrastructure 适配器」。落败。
- **横幅/状态推送经 Core 端口**（`IUpdateUi`）：委托假件已可测，端口不解锁新测试（MVP 判据：不为端口而端口）。落败。

## Consequences

- 编排策略（dev 门禁/对账清扫/装前 SHA 复取序/横幅去重/后台异常收口）首次有回归网；后续自更新编排改动只落 Core+tests（LIGHT 面）。
- `UpdateOptions` 迁移动了 Infrastructure 公共面（`ReleaseMetaClient`/`UpdateInstaller` 等消费方随全局 using 收敛）；`UpdateOptions.Load` 退役，装载语义（缺文件/坏 JSON fail-safe 回退默认）原样落于适配器。
- 组合根 new 计数触顶 16/16：后续根装配面新增自有类型即超闸——届时应走域内工厂或继续下沉注册，而非提额。
- 横幅去重测试依赖后台 `Task.Run` 时序：以 `WaitUntilAsync` 有界轮询（10ms 步进、2s 上限后断言）收敛，不引入固定 sleep。
- 评审裁定留痕：R1 五条 Suggestion 全采纳落地（rid 捕获进 Load、假件 `OnCall` 属性改构造注入、`Downloaded` 形状收窄至断言面、假件 `AssetExists` 收敛、本 ADR 时序措辞对齐）；R2 零发现；R3 两 Blocker（类型计数、门禁规则 ID）修复后验轮闭合。

## Testing

- `dotnet test` 867/867 全绿（Core 177 = 170 + 7 新增）；build 0 警告；format 0 差异；compose-root（16/16）/code-health/code-conventions/readme-badges/adr-format/md-links 门禁绿。
- 门禁自证：tier 判定 FULL（触组合根文件 `DesktopBootstrap.App.cs`），R1/R2/R3 三路简报齐（冻结树 `2b40332b`→验轮 `df64994a`），R3 验轮 0 发现。

## Related

- 总纲：`.plan/整改方案-三平台震荡后结构清理-2026-09-27.md` §4.1 表 #12、§4.2「更新栈」行、§4.6「无测试的关键策略类」。
- 挂账兑现：[d4-cleanup-batch](../../../../.agents/notes/implemented/architecture/2026-09-28-d4-cleanup-batch.md) 挂「`UpdateHttpClient` 归位随 D3-b」——本批以「迁 Core 用例 + Infrastructure 适配」兑现（口径从整改方案 §4.1 表 #12，比该条挂账的「迁 Infrastructure」多一跳），归位由适配器收口 `UpdateHttpClient.Create` 达成。
- 记载改写：[value-flow-batch1-state-sinking](../../../../.agents/notes/implemented/architecture/2026-09-15-value-flow-batch1-state-sinking.md) 的「HttpClient 构造随协调器迁出组合根」自此改写为构造收口进 `UpdateStackAdapter`（组合根仍零 HttpClient）。
- 跟值：[compose-root-gate](../../../../.agents/notes/implemented/process/2026-09-28-compose-root-gate.md) 记「根 new 计数 15/16」——本批随 `new UpdateStackAdapter` 进组合根至 16/16（触顶预警见 Consequences）。
- 先例形态：[runtime-supervisor-core-port](../../../../.agents/notes/implemented/architecture/2026-09-28-runtime-supervisor-core-port.md)（D3-a，端口只收敛消费集）。

---

<!-- 归档/状态迁移规则：
proposed → implemented：Status 改 implemented、移入 implemented/<class>/，## Proposal 改写为现在时的 ## Decision，Acceptance criteria/Risks 折叠进 ## Consequences（或现在时的 ## Testing/## Verification）。
proposed → rejected：Status 改为 "rejected — <一行理由>"，文件冻结。
归档：移入 archived/<class>/，Status 下插入 "Archived: YYYY-MM-DD"，之后永久冻结。 -->
