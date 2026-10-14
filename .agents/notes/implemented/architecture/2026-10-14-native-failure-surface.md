# Agent Note: 失败面原生化与恢复收口导航按承载面退场

Status: implemented

Review: FULL/2026-10-15/R1=ok R2=ok R3=ok

## Problem

恢复期失败面以脚本覆写活动页 `documentElement.innerHTML`，页面与其 SPA 状态被毁，故恢复成功后必须补一次回代理根的收口导航——而窗口 URL 恒为回环代理根、路由不在 URL 里，这次导航必然丢弃原路由（[recovery-navigation-gate-retirement](2026-10-14-recovery-navigation-gate-retirement.md) Decision 3）。上游把恢复放在页面内与原生层（官方 desktop 的 `fatal-recovery` 用原生对话框承载致命失败、`will-navigate` 只在 URL 不同时放行）。失败面不碰活动页 DOM，收口导航即失去唯一理由；而承载面切不到原生时（无对话框工具）、或原生通道自己故障时，页面仍须有承载面。

## Decision

1. **原生承载优先、退出意向倒置极性单问**：`Core.Recovery.IFailureDialog` 端口 + `Ryn.Plugins.Dialog` 0.38.0 适配（Windows `MessageBoxW` / macOS `osascript` / Linux `zenity`|`kdialog`）。插件确认通道的 `false` 同时表示「用户否定」与「通道故障」（Linux 探不到工具 / 进程起不来 / 非三平台 / osascript 出错一律 false）——故原生面问「要退出应用吗？」，**破坏性动作只挂显式 `true`**：任何通道故障都落回继续自动恢复。退出动作与托盘同一条顺序契约（先 `CloseGate.ApproveExit` 再 `IRynWindow.Close`）。
2. **回落页内恢复面，异常也回落**：`Core.Recovery.DialogToolAvailability` 纯函数先判平台与 PATH（Windows/macOS 恒可用；Linux 需 zenity/kdialog 之一且带执行位——插件探针口径），不可用即回落 `RecoveryPageBuilder`；原生通道抛异常时同样回落该面。失败面不静默消失。
3. **收口导航按承载面退场**：`RevalidateAfterRecoveryAsync` 只重验铸币态（epoch 可能已换，token-free 路径）并解锁失败面闩；零壳导航只对不碰 DOM 的原生承载成立，回落页内恢复面覆写过 DOM 时仍补一次收口导航（同篇 Decision 3 的判据）。
4. **诊断自动导出**：原生确认模态只有两个按钮，无第三处放导出动作——原生承载下自动导出（`Core.Recovery.IDiagnosticsExport`，Infrastructure `DiagnosticsExportService`）并把路径写进正文；导出失败退到失败文案 + `host.log`。
5. **一次恢复周期一次模态**：闩在 `onRecovered` 解锁（连败不得按重试节拍刷出无穷模态）；另设「模态在飞」闩防未处置模态跨周期堆积；模态在池线程展示（插件命令是同步原语），不阻塞监督循环。
6. **词典拆分部 + 最小授权**：失败面文案入 `UiCopy.FailureDialog.cs`（`UiCopy` 改 `partial`）守 F1（单文件 ≤400 行）；`verify-ui-copy.py` 读 `UiCopy*.cs` 全组并把 `RecoveryDialogText` 纳入消费面。宿主走插件的静态调用，**不注册页面侧 `dialog.*` 命令面、不放行 `dialog` 能力位**（页面侧无调用者）。

## Alternatives considered

- **把退出挂在原极性的「否」上（落败）**：插件通道故障恒回 false、与「否」同形——无对话框、无用户动作即自行退出应用。
- **双问确认（未采用）**：把「否」再问一次可把误触默认按钮移出退出路径，代价是每次退出多一个模态；破坏性动作已只挂显式「是」，误触属可见对话框上的用户动作，本批判为不值得多一个模态。**改用 `dialog.message` 信息面（落败）**：该命令在 0.38.0 的 Windows/Linux 分支是空实现，失败面在三平台中的两处直接消失。
- **失败面加「导出诊断」第三动作（落败）**：原生确认模态只有是／否两个按钮；加按钮要么三平台自造原生框（P/Invoke 三套），要么回到 DOM 覆写。自动导出 + 路径进正文以同等求助路径换掉按钮。
- **把恢复可用性押在页内自刷（落败）**：`awaitNewBoot` 是市场插件内部实现，壳不能依赖其就绪时点（免导航判据自 2026-10-04 起零命中已证不可依赖）。

## Consequences

- 买到的：原生承载下壳对窗口的导航归零、原路由落点不再由壳决定（回落分支保留一次收口导航）；失败面不碰活动页 DOM，求助路径由「自动导出 + 路径」保住；任何通道故障都只落回「继续自动恢复」或回落承载面。付出的：新增依赖 `Ryn.Plugins.Dialog`（与 Ryn 同版钉 0.38.0，本地 NuGet 缓存已备离线还原）；Linux 弹窗依赖系统自带 zenity/kdialog，打包面不加依赖声明；壳失去「导航已到达」这一恢复完成信号；插件的默认按钮是「是」（= 退出，不可选），误触 Enter 即退——换取「任何通道故障都不改退出」。
- 取代关系：[diag-masking-and-recovery-page](../bug-fix/2026-08-26-diag-masking-and-recovery-page.md) 的恢复页退为回落承载面（脱敏与导出入口语义保留，该篇已记 Erratum）；[recovery-navigation-gate-retirement](2026-10-14-recovery-navigation-gate-retirement.md) 第 2 条的收口导航据其 Decision 3 按承载面退场。本批是母篇 step-1「导航/监督用例下沉」的前置；承载插件 `Ryn.Plugins.Dialog` 0.38.0（NuGet 公开包，MIT），上游对照依据见该篇 Related。

## Testing

- `dotnet test` 全绿 0 警告：212 + 229 + 6 + 603 = 1050（基线 1037 → 1050，`scripts/test-baseline.json` 与 README 双语徽章同步）。新增 `DialogToolAvailabilityTests`（平台分支、PATH 空/命中、候选序）、`RecoveryDialogTextTests`（组成、空尾部、导出失败、英文无中文、裁剪）、`RynFailureDialogTests`（确认通道委托 seam、Linux 可用性两分支）。
- 「通道故障不改退出」由端口契约（false 只许落安全动作）与壳侧倒置极性单问承担，壳侧无单测缝。覆盖率本地按 CI 口径复算（`dotnet test -c Debug --collect:"XPlat Code Coverage"` + `scripts/coverage-summary.py` 并集）：63.88 → 63.71（新承载面的平台分支与集成面不可单测：`RynFailureDialog.IsExecutable` 的 Unix 执行位分支、`DiagnosticsExportService.Export` 副作用路径、监督循环内模态门控/回落分支收口导航；断言零删除）。门禁：code-health F1、compose-root 16/16、registration 11/11、code-conventions、ui-copy、increment-discipline 全绿。**实机未验**：三平台原生弹窗形态（按钮/裁剪观感）与回落承载面都需真实桌面或无工具环境（本机 PATH 有 zenity，走原生档；CI 无触发恢复期失败面的作业）。
