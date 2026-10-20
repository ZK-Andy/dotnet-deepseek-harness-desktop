# Agent Note: 宿主边界端口补齐与诊断导出收单路径

Status: implemented

Review: FULL/2026-10-20/R1=ok R2=ok R3=ok

## Problem

三处外部交互在壳侧（Presentation）直调基础设施静态面，绕过 Core 端口：

- `AutostartCommandRouter` 直调 `Autostart.IsEnabled/SetEnabled`（注册表 / XDG desktop entry / plist 都在静态类里）。
- `RynNavigationCallbacks` 与 `ExternalLinkCommandRouter` 直调 `SystemBrowser.Open`（`xdg-open` / shell execute）。
- `DesktopDiagnosticsCommandRouter` 直调 `DiagnosticsExporter.Export/ExportWithFallback`——而同一导出器**已有**
  Core 端口 `IDiagnosticsExport`（失败面在用），于是同一能力有静态与端口两条路径。

后果有三条：R3「边界抽象完备」不成立（外部交互不经接口）；注册点纪律闸的 Presentation 静态调用计数
10/帽 11 已近顶（新增一处即红）；诊断导出的「同一导出器、两个入口」使失败语义分叉（端口面失败回空串，
静态面失败抛异常由路由兜）。

## Decision

- **新增 Core 端口**（`Core/Platform/`）：`IAutostart`（`IsEnabled` / `SetEnabled`）、
  `ISystemBrowser`（`Open(url)`）；**Infrastructure 实现**：`AutostartService`、`SystemBrowserService`
  （薄适配器，平台面逻辑留在原静态类里，不动其行为）。
- 壳侧改为经端口：`AutostartCommandRouter` 取 `IAutostart`；`RynNavigationCallbacks` 与
  `ExternalLinkCommandRouter` 取 `ISystemBrowser`（原先的 `Func<string,bool>` 委托缝与「null 即用系统浏览器」
  的缺省一并去掉——缺省打开器本就是那条静态直调）。
- **诊断导出收单路径**：`IDiagnosticsExport` 的返回类型由 `string`（路径/空串）改为
  `DiagnosticsExportOutcome`（成功带路径 / 失败带原因）；`DiagnosticsExportService` 实现它并承接
  home/输出目录/版本的解析；`DesktopDiagnosticsCommandRouter` 只经端口拿结果并映射帧（成功 `{"path":…}`、
  失败 `{"error":…}`），`RecoverySurface` 取同一结果的路径。原先路由直调的 `DiagnosticsExporter` 静态面
  只剩 Infrastructure 内部（`DiagnosticsCli` 与服务实现）在用。
- 装配：端口实现在注册处登记（`ShellCommandRegistration.AddExternalLinkRouting` / `AddAutostartCommands` /
  `RecoveryRegistration.AddDiagnosticsCommands`），路由经容器解析端口。
- 测试侧新增两个替身（`FakeSystemBrowser` / `FakeDiagnosticsExport`），路由测试改注入端口替身；
  真实导出行为（写出可读 zip）的断言落在经服务构造的路由用例上。

## Alternatives considered

- **维持现状不修**（壳侧继续直调静态面）：落败——R3 边界抽象不完备是审计认定的偏离项，且注册点纪律闸的静态调用
  计数已到 10/11 帽：再有一处直调即红，说明这条边界已在事实上触顶；诊断导出的双路径还会让同一能力的失败
  语义分叉（空串 vs 抛异常）。
- **把静态类整体改成实例类并注册进容器**（`Autostart` / `SystemBrowser` 本身）：落败——那两个静态类的其余成员
  （条目文本构造器、`BuildProcessStartInfo` 等纯函数）被基础设施测试直接引用，改实例类会把测试面与调用面一起翻；
  薄适配器 + 端口既封住边界，又不动既有静态面的行为与测试。
- **保留 `Func<string,bool>` 委托缝，只把默认实现换掉**：落败——委托缝不是端口：注册处仍要引用基础设施静态面
  才能提供默认实现，边界只是换了个形状；端口化后注册处注入的是 Core 端口，实现细节不外泄。
- **诊断导出保留两条路径**（静态面给页面命令面、端口给失败面）：落败——同一导出器两套失败语义正是本批要消除的；
  统一到端口后，页面命令面拿到的是失败原因（比空串可展示），失败面拿到的仍是路径（空串表示失败）。
- **端口方法带上 `outputDirectory` 等参数**（把解析策略上提到调用方）：落败——home/版本/输出目录的解析是
  基础设施策略；上提到壳侧会把实现细节漏回 Presentation。
- **改用 Roslyn 分析器把「Presentation 不许直调基础设施静态面」做成编译期诊断**：落败——本仓已有 DSH0001
  （空 catch）分析器先例，但该诊断要区分「基础设施类型」与「壳侧自有类型」，分析器需知道 Infrastructure 的
  公开面清单（随每次改 Infrastructure 漂移）；现成形态是注册点纪律闸（按仓内既定静态调用清单判定），
  本批把计数从 10 降到 3 已是同一判据上的实质改进。

## Consequences

- 三处外部交互都经 Core 端口（本批范围：自启、系统浏览器、诊断导出）；注册点纪律闸的 Presentation 静态调用计数
  由 10 降到 3（本批移除 7 处：`Autostart.IsEnabled`/`SetEnabled`、`SystemBrowser.Open`×2、
  `DiagnosticsExporter.Export`/`ExportWithFallback`、`HarnessRuntimeHost.ResolveDshHome`；余下 3 处是既有面，不在本批范围）。
- 诊断导出单一路径：失败面与页面命令面共用一个实现与一套结果形态（成功/失败 + 原因），失败语义不再分叉。
- `RynNavigationCallbacks` 与 `ExternalLinkCommandRouter` 的构造函数签名变了（端口替委托）——本仓非已发布 API
  面（csproj 未打包为 NuGet），外部不受影响；仓内调用点与测试已同步。
- 端口实现留在 Infrastructure 的静态类里（行为未变），薄适配器只做转发——平台逻辑的测试面不变。
- 测试替身两个（浏览器、诊断导出）落在测试工程 `Fakes/` 下：路由测试不再触碰真实浏览器与真实 zip。

## Testing

- `dotnet build` + `dotnet test` 全绿（用例数与覆盖率基线以 `scripts/test-baseline.json` 为准）；原先直调静态面的用例改为注入端口替身，断言口径不变
  （外部链接仍断言「打开的 URL」「失败回 null 帧」「抛异常不外抛」；诊断路由仍断言「成功帧含 path 且 zip 可读」
  「失败帧含 error 且有日志」）。
- 注册点纪律闸实跑：变更前树（`f440639`）报 `infra static-call count: 10`，本批 index 树报 `3`（帽 11）——移除的 7 处逐条对上
  （`Autostart.IsEnabled`/`SetEnabled`、`SystemBrowser.Open`×2、`DiagnosticsExporter.Export`/`ExportWithFallback`、
  `HarnessRuntimeHost.ResolveDshHome`）。
- 组合根闸、尺寸闸、契约扫描全绿（新增端口与适配器未触装配面方向）。

## Related

- [open-external-links-in-system-browser](../bug-fix/2026-08-21-open-external-links-in-system-browser.md)——
  [ryn-navigation-callbacks](../feature/2026-08-28-ryn-navigation-callbacks.md)——两篇旧篇的「打开器经委托注入 /
  默认打开器收敛到共享 `SystemBrowser`」已由本篇的端口化取代（两篇已插 Erratum）。
- [compose-root-form-separation](../architecture/2026-09-28-compose-root-form-separation.md)——注册面下沉为按域 `AddXxx`，本批的端口登记沿用该形态。
- [native-failure-surface](../architecture/2026-10-14-native-failure-surface.md)——失败面自动导出诊断包（`IDiagnosticsExport` 的第一个消费者）。
- [host-command-ids](2026-10-20-host-command-ids.md)——同批的命令 ID 单一事实源（本批路由的 `CommandName` 也走那处常量）。
