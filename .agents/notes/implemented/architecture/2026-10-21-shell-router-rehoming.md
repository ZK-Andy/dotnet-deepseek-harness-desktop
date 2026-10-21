# Agent Note: 壳级命令路由按域归位

Status: implemented
Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

## Problem

壳工程按「目录即命名空间」组织（域目录挂项目根，见 [services-umbrella-retirement](2026-09-15-services-umbrella-retirement.md)），域目录已有 `Bootstrap/`、`PageBridge/`、`Recovery/`、`Tray/`、`Update/`。五个宿主命令路由的**实现文件**仍平铺在工程根目录、命名空间仍是根 `DeepSeek.Harness.Desktop`：`AppRestartCommandRouter`、`AutostartCommandRouter`、`CompanionLocaleCommandRouter`、`CompanionReportCommandRouter`、`UiLocaleCommandRouter`。同型同域的路由早已归位——`PageBridge/ExternalLinkCommandRouter`、`Bootstrap/BootstrapCommandRouter`、`Tray/CloseToTrayCommandRouter`、`Recovery/RecoveryCommandRouter`、`Update/DesktopUpdateCommandRouter`。

代价有三层：①「按域归位」在壳根留下五个例外，读者按目录找能力找不到实现；②`ShellCommandRegistration` 顶部挂着 `TODO(rehome-shell-routers)`——该行由组合根形态分离批（ADR compose-root-form-separation，提交 `df797cc`）引入时自带删除条件（「按域分文件归位时随迁并删除本行」），本批正是该条件成熟的时点；③壳根命名空间同时装着「组合根装配件」与「能力实现」，读者难以判断哪些类型是可动代码。

严重级定性：纯组织债（无运行时与编译正确性风险，无协议变化）。

## Decision

五个路由实现文件迁入**既有**域目录，命名空间随目录 1:1 改写；不新开任何目录。

| 路由 | 位置 | 命名空间 | 能力域判据 |
|---|---|---|---|
| `AutostartCommandRouter` | `PageBridge/` | `…Desktop.PageBridge` | 开机自启开关的页面设置面 |
| `CompanionLocaleCommandRouter` | `PageBridge/` | `…Desktop.PageBridge` | companion 语言上报 |
| `CompanionReportCommandRouter` | `PageBridge/` | `…Desktop.PageBridge` | 页面降级报障 |
| `UiLocaleCommandRouter` | `PageBridge/` | `…Desktop.PageBridge` | 页面拉取 UI 语言 |
| `AppRestartCommandRouter` | `Bootstrap/` | `…Desktop.Bootstrap` | 应用生命周期（关窗闸门 → 退出管道重启） |

- 归位判据是**命令归属的宿主能力域**，不是发起方：四个页面/companion 设置类命令与先例 `ExternalLinkCommandRouter` 同面（页面 ↔ 宿主命令桥），落 `PageBridge/`；`desktop.app.restart` 的能力域是应用生命周期，与 Core 侧退出编排 `Core/Bootstrap/AppExitOrchestration.cs` 同域，落 `Bootstrap/`。
- 注册面不动：`ShellCommandRegistration` 是这五个宿主路由的唯一注册点（仓内 `*Registration.cs` 共 8 处，分布于 5 个位置——`Bootstrap/` 4 处、`Recovery/`、`Tray/`、`Update/` 与壳根各 1 处；注册纪律闸的注册点判据＝文件名以 `Registration.cs` 结尾），`new XxxCommandRouter` 调用原地保留。
- using 一行未改：壳工程与 Desktop.Tests 的 csproj 全局 using 已覆盖全部域命名空间（含 `…Desktop.PageBridge`、`…Desktop.Bootstrap`）。
- `ShellCommandRegistration` 顶部 TODO 行删除；类注释的域清单补齐第五域（应用重启）并指向本 ADR。
- 组合根语义闸的 Bootstrap 域文件在册清单（`scripts/verify-compose-root.py` 的 `BOOTSTRAP_DOMAIN_FILES`）新增 `AppRestartCommandRouter.cs`：C8 规则要求 `Bootstrap/` 下的新文件显式在册（域代码或装配面二选一），迁入即在册——该闸的设计意图正是「搬进 Bootstrap 不得脱闸」。
- 零行为变更：命令 ID、帧形状、注册顺序、绑定类型均未动；跨命名空间移动不改变 wire 协议。帧源生成注册（`PageBridge/AppJson.cs` 的 `AppJsonContext`）与被注册类型（`AutostartCommandRouter.StateFrame`、`UiLocaleCommandRouter.UiLocaleFrame`）迁后同命名空间，注册项不变。

## Alternatives considered

- **五个文件留在根目录，只删 TODO 行**（落败）：TODO 的字面要求就是「按域分文件归位时随迁并删除本行」；只删行不迁＝把待做的事实抹掉，五个例外依旧。
- **新建 `HostCommands/` 域承载五个路由**（落败）：把「页面发起」当成一个能力域——同一目录会同时装下语言桥、报障口、开机自启与重启四类互不相干的能力；同型命令（页面调宿主）还会与 `ExternalLinkCommandRouter` 分家两处。这与「命令路由按特性随域走」的既定拍板相反。
- **`desktop.app.restart` 放进 `PageBridge/`**（落败）：按发起方归位会让「启动/退出编排」的域边界在 Core 与壳之间错位——能力域判据见 Decision 节的归位判据条。
- **归位时一并把这五个类型收窄为 internal**（落败·本批不做）：可见性收窄有独立判据与独立 ADR（[internal-visibility-narrowing](../simplification/2026-10-20-internal-visibility-narrowing.md)），本批只解决文件与命名空间归属；两件事夹带会扩大评审面而不增加决策质量。
- **为绕开 C8 门禁把 `AppRestartCommandRouter` 留在根目录**（落败）：C8 是显式声明纪律不是禁令——在册清单增一行即完成声明（见 Decision 节的组合根语义闸条）；为省一行清单而让路由脱离能力域，是拿组织一致性换清单整洁，方向反了。

## Consequences

- 公共面变化：五个类型从 `DeepSeek.Harness.Desktop` 移到 `DeepSeek.Harness.Desktop.PageBridge` / `DeepSeek.Harness.Desktop.Bootstrap`，**可见性仍为 public**。本仓不作 NuGet 包发布（交付形态 deb/rpm/dmg/安装器），仓外无程序集引用这些类型，故无对外破坏；仓内引用由既有全局 using 兜住（编译期即证）。
- 买到：壳根命名空间只剩组合根装配件与真正无域的孤件；「按域归位」在壳工程无例外；悬空 TODO 消除。
- 付出的：将来若出现仓外源码级消费者引用这五个类型的全限定名，需改 using 或加 `global::` 前缀——当前不存在这类消费者。
- 挂账（不在本批）：①`ShellCommandRegistration` 本身仍是根目录文件——是否按域下沉为 `PageBridge/`、`Bootstrap/` 各自的 `*Registration.cs`（注册点分散）属另一决策；②组合根语义闸的「域文件须在册」不变量只覆盖 `Bootstrap/`（C8 按 `src/Bootstrap/*.cs` 扫），本批迁入 `PageBridge/` 的四个路由无对等在册闸；③在册清单没有对称的「在册但文件不存在」死项校验，文件改名或删除会让在册项静默漂移。②③属门禁判据扩展，须自带 ADR 与三审，不在本批夹带。

## Testing

- `dotnet build` 0 警告 0 错误；`dotnet test` 全绿 1093/1093（与 `scripts/test-baseline.json` 基线一致：Desktop.Tests 237 + Core.Tests 234 + Infrastructure.Tests 616 + Analyzers.Tests 6），覆盖率 67.07% 不变。
- 门禁全绿：`verify-adr-format`、`verify-md-links`、`verify-doc-budgets`、`verify-code-health`、`verify-code-conventions`、`verify-compose-root --enforce`（含 C8 的 Bootstrap 域文件在册检查）、`verify-registration-discipline`、`verify-increment-discipline`。
- 评审档机器判定 **FULL**：改动面含 `scripts/verify-compose-root.py`（`verify-*.py` 属 FULL 触发路径）⇒ 走 R1（简化候选）/R2（代码评审）/R3（ADR 归档纪律）三路评审；评审证据行按 `verify-review-tier` 的头区约定（首个 `## ` 之前）写在 `Status:` 之后一行——正文不复写该串（判别符纪律：全仓证据行只存在于头区）。

## Related

- [services-umbrella-retirement](2026-09-15-services-umbrella-retirement.md)（implemented）：域目录挂根与「目录即命名空间 1:1」的拍板出处；其目标树中「根留 Autostart/CompanionLocale 两孤件路由」已被本 ADR 的落点取代（该篇以 Erratum 行记录，正文与决定不动）。
- [internal-visibility-narrowing](../simplification/2026-10-20-internal-visibility-narrowing.md)（implemented）：可见性收窄的判据与独立批次。
