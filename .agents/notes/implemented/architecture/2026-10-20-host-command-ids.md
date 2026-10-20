# Agent Note: 命令 ID 单一事实源（Core 强类型常量 + 伴生插件契约校验）

Status: implemented

Review: FULL/2026-10-20/R1=ok R2=ok R3=ok

## Problem

宿主↔页面的命令名（`desktop.update.getState`、`app.openExternal` 等 17 个）是跨包线协议：壳侧路由按它分派，
伴生插件（`plugins/dsh-desktop-companion`）与壳内引导页按它调用。此前两侧各自硬编码字面量：

- 壳侧：`AutostartCommandRouter.CanRoute`（`command is "desktop.autostart.getState" or …`）、
  `DesktopUpdateCommandRouter.CanRoute` 与其 `switch`、`CloseToTrayCommandRouter` 同款，外加 9 个路由各自的
  `public const string CommandName`——同一个命令名在一处出现两三次。
- 插件侧：`client/client.js` 里 31 处命令 ID 形态字面量（其中 22 处在代码、9 处在注释），覆盖 13 个不同 ID；
  壳内引导页 `wwwroot/index.html` 另有 2 处。

改一侧不改另一侧 = 静默失效：没有编译错误、没有测试失败，只有一个点了没反应的按钮或一条永远不触发的分支。
审计把这条列为「命令 ID 裸串跨包」，且本仓约定「跨界 ID 用强类型/Branded，禁止裸 string 跨包传递」。

## Decision

- **单一事实源** = `src/DeepSeek.Harness.Desktop.Core/Ipc/HostCommandIds.cs`：17 个命令/事件 ID 各一个
  `public const string`（协议常量，故为编译期常量，可进 `is`/`case` 模式匹配）。
- 壳侧一律引用该常量：路由的 `CommandName` 常量改为 `HostCommandIds.X` 赋值，`CanRoute`/`switch` 的
  字面量改为常量模式，事件名（`desktop.tray.event`、`desktop.externalLinkOpenerFailed`）同列同源。
- 测试侧同样引用常量（断言命令名的用例改用常量；负例用的「非本路由命令」字面量保持原样——它们不是已知 ID）。
- **机器门禁** `scripts/verify-host-command-ids.py`（三条判据）：
  ① 已知 ID 的**整串**字面量不得出现在 Core 常量文件之外的 C#（含 tests）——壳侧与测试侧都走常量；
  ② 伴生插件 JS 与壳内引导页 HTML 里的命令 ID 形态字面量必须在已知集合内——插件侧不得自造命令；
  ③ 每个已知 ID 都要有引用面（壳侧常量用法或插件/引导页字面量）——常量不得悬空。
  判据只认「整串字面量」：内含 ID 的数据串（如上报 payload 里的 scope 值）不算命令引用，不判红。
- 门禁接线：`ci.yml` 的 docs job（自测 + 全量）、`pre-commit` 快检、`docs/testing.md` 门禁表与执行点表登记。

## Alternatives considered

- **维持现状不修**（两侧继续各自硬编码）：落败——跨包改名静默失效是「最阴的一类」故障：没有编译期保护、
  没有测试信号，只有用户点了没反应；本仓的 IPC 强类型约定也明确禁止裸 string 跨包传递。
- **只加注释约定**（要求人不要写错）：落败——约定不是判据；改一侧漏另一侧仍是静默失效，而这类漂移只有机器拦得住。
- **生成式方案**（从单一源生成插件侧 JS 常量表）：落败——插件是独立 npm 包，生成物要随包发版、版本对齐成本高；
  校验式（门禁比对两侧）已能把漂移在 PR 里挡住，且不引入生成物同步面。
- **把命令名做成强类型值对象**（`readonly record struct CommandId`）：落败——Ryn 的 `ICommandRouter` 契约就是
  `string command`（上游接口不可改），值对象只会在边界处再转回字符串；本仓已有的强类型实践是
  「端口 + 类型化帧」（`AppJsonContext` 源生成），命令名走编译期常量已是同等强度的保护。
- **改用 Roslyn 分析器把「命令 ID 字面量」做成编译期诊断**：落败——诊断要在编译器内识别「这是命令 ID 字面量」，
  须对 ID 集合有编译期可见的来源（Core 常量在分析器侧不可达），分析器只能退回字面量模式匹配——与本门禁同一条路，
  却多一层分析器装载与版本对齐成本；本门已是可执行判据且已进 CI/pre-commit。

## Consequences

- 命令名改一处即全链生效：Core 常量是唯一出处，壳侧引用它，插件侧与引导页由门禁比对。
- 门禁会拦下三类漂移：壳侧复写已知 ID、插件侧自造命令、已知 ID 无人引用（死契约）。
- 「整串字面量」判据的边界是明确的：数据串内含 ID 不算引用（上报 payload 里的 scope 值即此类），
  免得门禁把数据当代码判红。
- 伴生插件若新增命令，必须先在 Core 常量文件登记（否则门禁判红）——这是有意的摩擦：命令是跨包契约，
  两侧不同步就是坏发布。
- 插件包本身不因此变化（不改 JS 运行时代码），仅门禁把住它的字面量集合。

## Testing

- 门禁自测 5/5 绿（离线夹具：合规样例 / 壳侧整串字面量 / 插件侧未知命令 / 注释里的 ID 不算引用 / 数据串内含 ID 不判红）。
- 变异实测两处均判红：把 `UiLocaleCommandRouter` 的常量改回字面量 → 「应改用 HostCommandIds 常量」；
  给插件 `client.js` 加 `invoke('desktop.notARealCommand')` → 「不在已知集合内」。
- 全仓实跑绿（壳侧 12 个路由 + `ShellCommandRegistration`（事件名）+ `RecoveryPageBuilder`（恢复页脚本拼 ID）
  + 8 个测试文件已改用常量；
  `desktop.cordis.yml` 是文件名不是命令 ID，不在集合内）。
- `dotnet build` + `dotnet test` 全绿（用例数与覆盖率基线以 `scripts/test-baseline.json` 为准）。

## Related

- [desktop-shell-self-update](../process/2026-08-22-desktop-shell-self-update.md)——`desktop.update.getState/check/install`
  三个命令面的由来。
- [companion-invoke-frame-contract](../bug-fix/2026-08-24-companion-invoke-frame-contract.md)——伴生插件 invoke 帧契约的由来。
- [open-external-links-in-system-browser](../bug-fix/2026-08-21-open-external-links-in-system-browser.md)——`app.openExternal` 的由来（Ryn 注入脚本与导航层共用）。
- [external-link-opener-failure-toast](../feature/2026-08-28-external-link-opener-failure-toast.md)——`desktop.externalLinkOpenerFailed` 事件面的由来。
