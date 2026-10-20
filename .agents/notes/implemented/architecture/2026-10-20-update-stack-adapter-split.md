# Agent Note: 自更新栈适配器按端口拆分

Status: implemented

Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

## Problem

`Infrastructure/Update/UpdateStackAdapter.cs` 一个具体类型同时实现 Core 的四个端口（`IUpdateEnvironment`／`IReleaseFeed`／`IPackageDownloader`／`IPackageInstaller`），装配层把**同一个实例**传进 `UpdateCoordinator` 的四个端口位四次。

后果有三：

- **改动理由互相牵动**：宿主事实（dev 门禁、配置、更新目录、平台判定、对账清扫、ready 持久化）与 HTTP 抓取、下载落盘、安装器派生是四类不同的变化理由，同住一个类时任一理由的改动都会经过同一个文件，评审与回归都按「这个类」而不是「这个端口」划界。
- **边界读不出来**：端口是 Core 侧的契约，实现却看不出哪个方法属于哪个端口——读 `UpdateStackAdapter` 的人要先回 Core 逐个接口对，才能知道「哪些方法是这个端口的」。
- **装配面没有逐端口的现成实现**：协调器的四个端口位本身已是独立接口——**逐端口替换能力拆分前就有**（`UpdateCoordinator` 的构造签名本就分四个接口，既有 `UpdateCoordinatorTests` 的 `FakeFeed`／`FakeDownloader`／`FakeInstaller`／`FakeEnvironment` 即证据）；缺的是**实现侧的落点**：装配层手上只有一个「什么都能干」的具体类型，要让其中某个端口换个实现，得另写一个实现类。

2026-10-20 的功能闭环与架构一致性审计把它判为「值得改」（本地审计稿 `.plan/审计报告-功能闭环与架构审计-2026-10-20.md` §4「架构要不要改」第 6 条；该稿未入库，入库后读者可按本段复述内容核对）。

## Decision

**一端口一实现**，四个实现同住 `Infrastructure/Update/`：

| 端口 | 实现 | 职责 |
|---|---|---|
| `IUpdateEnvironment` | `UpdateEnvironmentAdapter` | 宿主事实：dev 门禁、配置装载、更新目录、平台判定与 RID、对账清扫、ready 持久化工厂（不触网络） |
| `IReleaseFeed` | `ReleaseFeedAdapter` | release feed 抓取（版本比对在 Core 的 `UpdateVersion`；`ReleaseMeta` 只做 expanded_assets 解析与资产挑选） |
| `IPackageDownloader` | `PackageDownloaderAdapter` | 安装包下载与装前 SHA 复取（转发给 `InstallerDownloader`） |
| `IPackageInstaller` | `UpdateInstaller` | 安装器派生与授权观察窗（**直接实现端口**，不设转发壳） |

- **HTTP 出口仍唯一：类内无第二个构造入口 ＋ 接线回归的反射判据共同兜住**：feed 与下载两家共用 `UpdateHttpClient.Shared`（进程级单例）。拆之前是同一个适配器里那个懒构造的 `HttpClient`；拆后 `Shared` 是进程级静态属性，`PackageDownloaderAdapter` 的 worker 在字段初始化时读取它，故客户端在**装配期**（而非首次 HTTP 调用）构造——构造只建句柄、不触网络，实例与超时语义（无整体超时）不变，构造时机的这一点差异不影响任何行为；`UpdateHttpClient` 由 public 收为 internal（生产消费者在 Infrastructure 内，测试经既有 `InternalsVisibleTo`），其原有的 public 工厂方法**取消**——客户端直接落在共享实例的属性初始化上，类内没有第二个构造入口（工厂若可被外部调用，「连接池 1→2」就不由可见性兜底）。其余**静态**构造路径由接线回归的反射判据兜住；未覆盖的形态见 `UpdateHttpClientExposureTests` 类注释的「未钉住的部分」。
- **装配层四个实例分别注入**：`PreBuildRegistration` 里 `new UpdateEnvironmentAdapter/ReleaseFeedAdapter/PackageDownloaderAdapter/UpdateInstaller` 各一（`HostLog.Write` 随传，日志行文出口不变），再按端口位传给 `UpdateCoordinator`——同一实例传四遍的写法消失。
- **构造清单同步**：`verify-compose-root.py` 的 C7 装配期构造清单以四个新名替换 `UpdateStackAdapter`（名单是「装配期允许出现的具体类型」的唯一在册处，开名单需另立 ADR）。

## Alternatives considered

- **保持单型适配器不拆**（落败）：审计已把它列为「值得改」；四端口一实现的牵动成本不会随时间下降，且装配面没有逐端口的现成实现（测试侧的逐端口替换能力本就有——协调器构造签名分四个接口，既有假件即证据）。
- **四个端口各配一个薄转发壳**（落败）：安装器端口的转发壳不隐藏任何细节——目标 `UpdateInstaller` 本就是公开的安装执行器，转发壳只多做一层形参搬运；且与仓库既有惯例相悖（端口实现直接实现端口，如 `FileReadyPersistence`）。安装器端口因此由 `UpdateInstaller` 直接实现（`log` 入构造器），转发壳删除；另两个壳保留——它们的 worker 形状与端口形状不匹配（`ReleaseMetaClient` 把 `UpdateOptions` 绑在构造器而端口按调用传，`InstallerDownloader` 的 SHA 方法名与端口方法名不同），壳是必要的。
- **拆四类但各自新建 `HttpClient`**（落败）：连接池从 1 变 2，下载与 feed 各占一套连接——与拆前语义不等价，属于顺手引入的行为变化；共享单例本就在 Infrastructure 内可达，无需让根层介入。
- **把四端口并成一个宽端口**（落败）：端口是 Core 侧的既有契约，合并等于把 ISP 诉求原样退回，且要改 `UpdateCoordinator` 的构造与状态机接线——改动面远大于拆分。
- **Core 侧加一个 `UpdateStack` 门面统一注入**（落败）：门面即回到「一个类型四个端口」，只是把耦合从 Infrastructure 搬到 Core。
- **把四个实现收进一个工厂方法**（落败）：少写几行 `new`，但多一层间接；装配层显式列出四个端口位的注入关系本就是它该有的样子（组合根只装配）。
- **用源文本锚钉接线不变量**（落败）：锚（数静态成员写法、剔注释与字符串字面量）追不完形态——注释、字符串、别名、泛型、数组、实例作用域每轮都要补漏，且良性重构会假红。这条不变量在**已编译程序集**上可枚举（静态成员的类型逐个看），故判据走反射枚举，源锚不引入。

## Consequences

- 买到的：端口与实现一一对应；改任一端口的理由只落在自己的文件；四个端口各有现成实现类；HTTP 出口仍唯一。
- 付出的：`UpdateStackAdapter` 这个类型名在仓库消失（相关旧篇同批插 Erratum，指针改指本篇）；装配层由一处 `new` 变四处（净 +3；C7 清单在册，根集 `new` 计数不变——这四个构造在装配面不在根集）；文件面上由 1 个适配器文件变为 3 个适配器文件加 1 个改造后的 worker（`UpdateEnvironmentAdapter`/`ReleaseFeedAdapter`/`PackageDownloaderAdapter` ＋ `UpdateInstaller`，其中第一个是改名而来），共享客户端文件 `UpdateHttpClient.cs` 本就存在、本批只改了可见性并加了 `Shared` 属。
- 方法体逐句搬迁：四个端口的成员与原类一一对应，无逻辑改写；`UpdateRid()` 随宿主事实归 `UpdateEnvironmentAdapter`；`UpdateInstaller` 的 `log` 由调用期参数改为构造器注入，签名与端口方法对齐。
- **HTTP 出口的净 diff 面**：原 `UpdateHttpClient` 是 `public static class` 且带 `public static HttpClient Create()`；本批把它收为 internal、删掉那个 public 工厂、改为共享实例直接初始化——「共享客户端 + 单一构造入口」是本批确立的形态。
- C7 闸的两条覆盖缺口（静态工厂构造面无通道、清单只报「构造了但不在册」而不报反方向）是闸的既有属性、非本批引入；清单与归属只记在闸脚本内联注释与待办区，本篇不复述。
- C7 装配期构造清单本批只做**换名**（`UpdateStackAdapter` → 四个新实现）；`CaptionBarOptions` **留在册**——它在装配面确实被构造（`CaptionBarOptions.Load(...)`），闸的三条通道看不见静态工厂形态属上述静态工厂构造缺口（待办区 C7 缺口①），不是「该类型不在装配面」。

## Testing

- 端口成员对账：四个端口的全部成员在四个实现中一一落位（`IsEnabled`/`LoadOptions`/`CurrentVersion`/`ResolveUpdatesDir`/`UpdateRid`/`DetectPackageKind`/`PruneStale`/`CreateReadyPersistence`、`FetchLatestAsync`、`DownloadAsync`/`FetchExpectedSha256Async`、`LaunchAsync`），方法体自原类逐句搬迁。
- `dotnet build` 0 警告 0 错误；`dotnet test` 全绿，测试数与 `scripts/test-baseline.json` 基线一致（基线与 README 双语徽章随本批的用例增删同批更新）。
- 增量闸：变更集 gross ≤ 默认帽 600、fanout ≤ 15（帽值口径见 `scripts/verify-increment-discipline.py`）；本批不需要一次性豁免，`pre-commit`／`pre-push`／`ci.yml` 三处的豁免指针保持原状。
- 接线回归（`tests/DeepSeek.Harness.Desktop.Infrastructure.Tests/Update/UpdateHttpClientExposureTests.cs`）：判据面与**未钉住的部分**以该用例的类注释为唯一家，本篇不复述。
- 门禁全绿：`verify-compose-root --enforce`（含 C7 清单核对）、`verify-registration-discipline --enforce`、`verify-code-health --enforce`、`verify-code-conventions --enforce`、`verify-increment-discipline --staged --enforce`、`verify-adr-format`、`verify-md-links`。

## Related

- [update-coordinator-core-port](../architecture/2026-09-28-update-coordinator-core-port.md)——四端口与协调器的由来（本篇拆的是它的实现侧）。
- [host-boundary-ports](../architecture/2026-10-20-host-boundary-ports.md)——同批的边界端口补齐（自启/系统浏览器/诊断导出）。
