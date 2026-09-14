# Agent Note: clean-architecture-b1-core-extraction（三项目解组织 B1：骨架 + Core 纯逻辑迁移）

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

父 ADR [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md) 拍板 B1–B4 四批迁移；B1 = 三项目 slnx 骨架 + 纯逻辑组件移入 Application Core。迁移前单项目 84 文件，端口/适配器/用例/纯逻辑混居 `Services/`，Core 不存在、依赖方向无编译器强制。本批在零行为变更前提下落地 B1：`DeepSeek.Harness.Desktop.Core`（net10.0，**项目引用必须为空**）承载纯逻辑组件；主工程 → Core 单向引用。

## Decision

**新建 `src/DeepSeek.Harness.Desktop.Core`（Application Core，零项目引用）；slnx 三项目骨架；13 个纯逻辑组件迁入 Core。命名空间保持 `DeepSeek.Harness.Desktop.Services[.Update]` 不变——调用方零改动、A2/A4/A5 命名空间门过渡期继续有效（B4 升级为项目引用断言）。**

文件移动映射（批内台账）：

| 源（主工程 Services/） | 去向 |
|---|---|
| ExternalLinkPolicy.cs | Core（Ryn cref 改平文） |
| UiLocale.cs + UiCopy.cs | Core（两者纯字符串耦合随迁——eShopOnWeb Core 亦有 Resources 先例；verify-ui-copy 词典路径同步指 Core） |
| PreinstallGate.cs | 拆分：`PreinstallChoice`+`PreinstallChoiceGate` → Core；`PreinstallFrame`（UI 推送帧，AppJsonContext 注册）留主工程 `PreinstallFrame.cs` |
| PageHealthMonitor.cs | 拆分：`PageHealth`+`PageHealthTracker`（判定核）→ Core；监视器（Ryn 桥）留主工程 |
| RuntimeLineage.cs + RuntimeLineage.Probes.cs | 拆类：策略核 → Core `RuntimeLineage`（去 partial，partial 不能跨程序集）；探针面 → 主工程 `RuntimeLineageProbes`（D005 白名单同步更名） |
| BundledPluginCatalog.cs | 拆分：`Entry`+`AssemblePending`（装配判定）→ Core；供给清单 `All` → 主工程 `BundledPluginSupply`（组合侧数据，引用 MarketInstallHelper） |
| PresetPluginCatalog.cs | Core（就位检测改走 Core 侧 ProfilePackageCheck） |
| PluginVersionCheck.cs | Core 原样（BCL 文件读取；D005 白名单按文件名继续覆盖） |
| Update/UpdateState.cs | Core；`UpdateStateFrame` 序列化随类型走 → Core 新增 `UpdateJsonContext`（源生成选项与 AppJsonContext 一致），AppJsonContext 摘除该帧注册 |
| Update/UpdateStateMachine.cs | Core + **IPersistence 端口化**：新增 `AssetExistsAsync`（状态机不再直触 `File.Exists`）；`HostLog` 直调改注入 `Action<string>? log`（组合根传 `HostLog.Write`） |
| Update/UpdateVersion.cs、Update/AppVersion.cs | Core 原样 |
| Update/ReleaseMeta（record 含静态 Pick 解析表） | 整体 → Core `ReleaseMeta.cs`；`ReleaseMetaClient`（HttpClient）留主工程 |
| MarketInstallHelper.Json.IsBundleInstalled + BundlesContain | 实现 → Core 新 `ProfilePackageCheck`；主工程方法保留为委托壳（既有调用方零改动，B2 摘壳） |

配套：主/测试 csproj 增 ProjectReference Core；Core InternalsVisibleTo 主工程+测试工程；`ArchitectureTests` 新增 **R2 种子测试**（反射断言 Core 程序集零 `DeepSeek.Harness.Desktop*`/`Ryn*` 引用，B4 升级为 csproj 项目引用断言）；测试 fake 补 `AssetExistsAsync`（语义与生产一致：按路径真实文件存在性）。

## Alternatives considered

- **命名空间一并改为 `DeepSeek.Harness.Desktop.Core.*`**：全仓 using 大面翻动、A2/A4/A5 门失效提前、行为零收益；B4 随 ArchitectureTests 升级一并做。落败（延后，非取消）。
- **RuntimeLineage 整类留主工程、策略核推 B2**：父 ADR 已把策略核划入 B1；探针经 `Func` 注入本就纯，拆类成本可控（改名 + 调用点限定名）。落败。
- **UiLocale 留主工程（UiCopy 在 B3）**：`OkLabel → UiCopy.OkLabel` 纯函数耦合，留主工程则 B1 白做半截；两文件皆纯、一起迁最诚实。落败（UiCopy 随迁，B3 仅剩横幅/恢复页等消费面）。
- **IsBundleInstalled 原地留 MarketInstallHelper、Core 清单经委托注入**：会改 `AssemblePending`/`PendingForFirstBoot` 签名并牵连调用方与测试；该方法本就是 profile 读取边界，落 Core 单点更贴 R3。落败。
- **UpdateStateFrame 留 AppJsonContext、ToJson 留主工程扩展方法**：帧契约与状态类型分离、调用点 `state.ToJson()` 悄悄变扩展方法语义；Core 自带源生成上下文 8 行即收。落败。

## Consequences

- 依赖方向开始被编译器强制：Core 引用任何外层（含 Ryn）即编译失败；R2 种子测试兜反射面。新代码一律先问「进 Core 还是边界」，D005 白名单进入只减不增轨道。
- **613/613、0 警告、format 门禁绿；零行为变更判据**：文件移动 = `git mv`/拆类，行为面仅两处等价改写（`File.Exists` → 端口 `AssetExistsAsync`（FileReadyPersistence 实现仍 `File.Exists`）、`HostLog.Write` → 注入委托（组合根仍传 `HostLog.Write`））。
- 过渡态已知项：verify-code-health F1–F4 仍单扫主工程（Core 文件尺寸由评审兜底，B4 收敛）；覆盖率/测试徽章基线随标准跟值批收口（最终 614/614、`4040/7306 = 55.30%`；[覆盖合并口径](../testing/2026-09-14-coverage-baseline-multi-project-merge.md)）。
- CI 工作流零变更（build/test 走 slnx；打包 publish 主工程自动携带 Core DLL）。

## Related

- [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)：父 ADR（B1–B4 路线）；[2026-09-14-clean-architecture-b4-finalization](2026-09-14-clean-architecture-b4-finalization.md)：B4 收口（命名空间升级兑现、D005 白名单退役）；[docs/architecture-standards.md](../../../../docs/architecture-standards.md)：规范正文。
- [2026-08-30-architecture-mechanization](../process/2026-08-30-architecture-mechanization.md)：A2/A4/A5、D004/D005 门禁通道（本批 D005 白名单两处同步）。
