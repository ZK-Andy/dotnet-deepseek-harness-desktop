# Agent Note: clean-architecture-b2-infrastructure（三项目解组织 B2：Infrastructure 适配器迁移）

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

父 ADR [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md) B1–B4 路线的第二批：B1 已落三项目骨架与 Core 纯逻辑，但 35 个端口/适配器文件（dsh 进程、shim、系统面、更新集成）仍居主工程，八类外部边界零端口位。本批在零行为变更前提下新建 `src/DeepSeek.Harness.Desktop.Infrastructure`（net10.0，**项目引用仅 Core**），主工程（Presentation）单向引用之，依赖方向 R2 对边界层生效。

## Decision

**新建 `src/DeepSeek.Harness.Desktop.Infrastructure`：只准引用 Core（反向即成环、编译器强制）；适配器实现在本工程、接口定义在 Core、组合根注入（R3 物理端口位）。命名空间保持 `DeepSeek.Harness.Desktop.Services[.Update]` 不变——调用方零改动、A2/A4/A5 命名空间门过渡期继续有效。Infrastructure 零 Ryn 包依赖（全族适配器只触 BCL 外部边界，Ryn 面属 UI 桥，B3 处置）。**

文件移动映射（批内台账；前 35 项为父 ADR 清单，后 6 项为依赖跟随）：

| 源（主工程） | 去向 |
|---|---|
| dsh 进程族：`HarnessRuntimeHost`×4 partial、`RuntimeBootstrap`×3、`RuntimeBootstrapOptions`、`RuntimeVersionGate`、`PluginProcessRunner`、`DshSubprocessScopeReaper`、`OrphanDshReaper` | Infrastructure |
| 依赖跟随：`HarnessUrlParser`（dsh stdout 解析）、`PluginInstallProbe`（复用 BuildStartPsi）、`EnvironmentHygiene`（spawn 净化）、`RuntimeLineageProbes`（/proc 探针面，B1 留主工程的既定去向即边界层） | Infrastructure |
| 引导与 shim：`CliShimBuilder`/`CliShimPath`/`CliShimRegistrar`、`MarketInstallHelper`×3、`DesktopProfileBootstrap`、`PluginProfileTransaction`、`BundledPluginSupply`、`DevEnvironment` | Infrastructure |
| 系统面：`SystemBrowser`、`LauncherActivation`、`RunMarker`、`Autostart`、`HostLog`、`SecretMasker`、`DiagnosticsExporter` | Infrastructure |
| 更新集成面：`UpdateInstaller`、`InstallerDownloader`、`ReleaseMetaClient`、`FileReadyPersistence`、`StalePackagePruner`、`UpdatePlatform`、`UpdateOptions` | Infrastructure |

拆解（跨层纠缠三处，均为等价改写）：

- **横幅构建器归表示面**：`RuntimeVersionGate.BelowFloorBannerScript`/`RunMarker.UncleanBannerScript` 依赖主工程 `DesktopBanner.Build`（后者消费 `AppJsonContext.JsString`），拆为 `DesktopBanner.BuildVersionFloorBanner`/`BuildUncleanExitBanner`；文案正文收编进 UiCopy 词典（`VersionFloorBannerText`/`UncleanExitBannerText`——落点 DesktopBanner 本就是 verify-ui-copy 消费文件，词条零行为差异，okLabel 走 `Build` 缺省 = 原「知道了」字面量）。
- **ReadyRecord 序列化随类型走**：`FileReadyPersistence` 改用 Core `UpdateJsonContext`（新增 ReadyRecord 注册，CamelCase+Serialization 与 AppJsonContext 一致，键名 version/assetPath 不变）；AppJsonContext 摘除注册。
- **MarkerFile 序列化随类型走**：Infrastructure 新建 `InfrastructureJsonContext`（选项与主工程一致）承接 run-marker.json 落盘帧；AppJsonContext 摘除注册。

配套：slnx 挂接；主工程/测试工程增 ProjectReference Infrastructure；Core `InternalsVisibleTo` 增 Infrastructure；ArchitectureTests 增 **R2 种子测试**（反射断言 Infrastructure 程序集零 Ryn*/主程序引用；反向依赖编译器已拦成环，本测试兜反射面，B4 升级为项目引用断言）；`verify-code-conventions` 扫描根增 Infrastructure（D005 白名单按文件名继续覆盖，零新增条目）；Core/Infrastructure csproj `NoWarn` 补 `EnableGenerateDocumentationFile`（主工程既有的同款抑制，B1 全量重编即现的存量噪音，随本批对齐）。

## Alternatives considered

- **UpdateOptions/DevEnvironment 留主工程、经注入传参**：`ReleaseMetaClient`/`UpdateInstaller`/`CliShimRegistrar` 直接消费其字段与常量，注入即改构造签名并牵连组合根与测试；两者本就是 env/配置读取边界，落 Infrastructure 单点更贴 R3。落败。
- **BundledPluginSupply 留主工程、解析器经委托注入 MarketInstallHelper**：供给清单与 `MarketInstallHelper.ResolveCompanionSpec` 互引成环，委托注入为拆环引入间接层；清单是装配侧数据、唯一消费者在边界层，整族同迁最诚实。落败。
- **横幅构建器随 RuntimeVersionGate/RunMarker 入 Infrastructure**：`DesktopBanner.Build` 消费 `AppJsonContext`（Presentation 帧）且横幅是 UI 注入面，落边界层违反依赖方向；拆到 `DesktopBanner` 工厂与既有「宿主横幅单一工厂」定位一致。落败。
- **MarkerFile/ReadyRecord 注册并入主工程 AppJsonContext 保持单上下文**：Infrastructure 类型消费主工程 internal 上下文即反向依赖，编译不可行；各自程自带 8 行上下文为 B1 先例。落败。
- **命名空间一并改 `DeepSeek.Harness.Desktop.Infrastructure.*`**：同 B1 同款取舍——大面翻动、A2/A4/A5 门提前失效、行为零收益，B4 随 ArchitectureTests 升级一并做。落败（延后，非取消）。

## Consequences

- 依赖方向三环闭合：Core（零引用）← Infrastructure（仅 Core）← 主工程（Presentation + 组合根）；边界适配器再引主工程符号即编译失败。新外部交互先问「端口进 Core 了吗」，D005 白名单继续只减不增。
- **614/614、0 警告、format 门禁绿；零行为变更判据**：40 文件 `git mv`；行为面仅三处等价改写（横幅构建器换家、两处 JSON 上下文换家——序列化选项/键名逐项比对一致）；UiCopy 词条为文案收编非改写。
- 过渡态已知项：A2/A4/A5 与 verify-code-health 仍单扫主工程（Infrastructure 类型退出扫描面，约束由编译器 + R2 种子测试接棒；A5/A2 升级 B4 收敛）；ArchitectureTests 对主程序集的扫描不再覆盖已迁类型（同上）。
- CI 工作流零变更（build/test 走 slnx；打包 publish 主工程自动携带 Infrastructure DLL）。
- 测试徽章基线 613→614 待下次 CI 跟值（标准跟值批）。

## Related

- [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)：父 ADR（B1–B4 路线）；[2026-09-14-clean-architecture-b1-core-extraction](2026-09-14-clean-architecture-b1-core-extraction.md)：B1 先例（命名空间保持/种子测试/上下文随类型走均沿袭）。
- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：规范正文（R2/R3 规则单一事实源）。
