# Agent Note: 失败面原生对话框三缺陷修复（正文压短 + 卡住才弹 + 孤儿框收敛）

Status: implemented

Review: FULL/2026-10-15/R1=ok R2=ok R3=ok

## Problem

2026-10-15 v0.6.9 实机（用户 Linux deb 安装）：插件市场更新（`POST /dsh-market/update` 07:30:27）触发 dsh 重启，失败面原生对话框（zenity）弹出，三症状：① 正文 ≈1300 字符——stderr 尾部 8 行全是 dsh peerDependency 不兼容重复警告（dsh 0.2.1-alpha.3 vs LSP 三插件的 alpha.2 peerDeps 循环重复），每行原文 600+ 字符 → 按 160 截后仍 ≈1300 字符怼进 `zenity --text`，按钮被顶出屏幕无法点击；② 监督循环不被模态阻塞（fire-and-forget），14 秒后市场接力续任者收养、自行恢复，问题过时但框仍在；③ `ConfirmAsync` 全链 `CancellationToken.None` + 退出编排不杀子进程，应用重启后旧框成孤儿（host.log 重启后无新对话框行）。附带：插件 0.38.0 只传 `--title`/`--text`（二进制反射实证），Linux/macOS 无图标——本地无手段，记上游需求。

## Decision

1. **正文总上限 + 去重**（`Recovery.RecoveryDialogText` 纯函数）：裁剪后全局去重（保首次出现序，重复记语言中立 ` (×N)` 后缀，不进 `UiCopy` 词典——与既有 `…` 同理）；从最新往最旧装到总 600 字符封顶，一行装不下则尾部节整体退场。原因/导出/动作说明恒在。
2. **只在卡住时弹**（`Core.Bootstrap.RuntimeSupervision`）：未恢复连续崩溃计数（`_consecutiveFailures`），恢复成功在 `OnRecoveredAsync` 复位（偶发崩溃永不攒到阈值，成功自愈的瞬态只记 host.log）；阈值 `FailureSurfaceStuckThreshold`（`RuntimeTimeouts` 配置家默认 3，options 记录不持默认值、组合点快照构造——沿 `BlockedLogEveryRounds` 同款）；≤1 回到每次都弹。
3. **孤儿框收敛**（`Core.Recovery.IDialogOrphanReaper` 端口 + `Infrastructure.Runtime.DialogOrphanReaper`）：插件无取消面（反射实证 0.38.0 `Confirm(title,message)→bool` 同步、无 Cancel/Close）→ 退出编排收口末尾 best-effort 收自己名下 zenity/kdialog 直接子进程（Linux `/proc` 亲缘 + 进程名双条件，零误杀方向）。注册在 `RecoveryRegistration`（R1b 注册处）；退出经 `AppExitOrchestration` 可选委托（既有单测兼容）。Windows 面是进程内 `MessageBoxW`（随进程走）、macOS 无 `/proc`（记已知缺口）——两处 no-op。
4. **图标走上游**：参数在插件手里（本仓无参数面）。

## Alternatives considered

- **彻底去弹窗（落败）**：真循环时无退出逃生口；原生通道下页内面不覆写 DOM，可见性归零——用户只能干看转圈或 kill -9。
- **保持每次崩溃都弹（落败）**：即本次事故——14 秒自愈的瞬态也被打扰。
- **直接砍 stderr 尾部（落败）**：首崩现场信息全丢；诊断包虽有但多一步。
- **Linux 自 spawn zenity 传图标/宽度（落败）**：kdialog 参数形状未知 + 重复插件职责。
- **macOS 也收孤儿（暂缓）**：无 `/proc`，pgrep 外挂失败面多；记缺口。
- **P3 传退出 CTS 进 Confirm 等待（落败/半修）**：插件 Confirm 无 token 重载（反射实证），且取消等待也杀不掉子进程——框还在，只是线程释放。

## Consequences

- 买到的：偶发崩溃零打扰（host.log 留痕可判读）；真循环仍有逃生口；长重复警告不再顶掉按钮；Linux 退出不再留孤儿框。付出的：阈值 3 为经验值（可调）；macOS 孤儿框 + 三平台图标未决（上游）；`_failureSurfaceShown` 闩与计数器叠加语义多一处需理解（注释已写）。
- 上游 skew（dsh alpha.3 vs 插件 alpha.2 peerDeps）本身非本仓问题：它只决定警告文本的长度，不决定壳行为；去重后 8 行压成 3 行。
- 取代关系：[native-failure-surface](../architecture/2026-10-14-native-failure-surface.md) Decision 5 的「一次恢复周期一次模态」语义不变，本篇在其上加「未达阈值不弹」与「退出收孤儿」两条；该篇 Testing 的「实机未验」由本次事故部分承接（原生 modal 已弹出、恢复链走通，剩余 P1–P3 复验转下次插件更新重启）。

## Testing

- `dotnet test` 全绿 0 警告：231 + 6 + 231 + 615 = 1083（基线 1067 → 1083，`scripts/test-baseline.json` 与 README 双语徽章同步）。新增：`RecoveryDialogTextTests` 去重/尾部节预算（+2）；`RuntimeSupervisionTests` 阈值门（第 3 次才弹，门住周期重启照跑）/恢复复位（+2，另把 4 个单崩溃旧用例钉阈值 1、在飞守卫用例按无恢复四轮重写）；`DialogOrphanReaperTests`（stat 解析/进程名判定/入口无抛，+8 theory 用例）；`RuntimeTimeoutsTests` 新键全键覆盖 +1 断言。覆盖率 63.71 → 64.79（本批新增纯函数基本 covered；`TryKill` 杀进程体与 `Reap` 枚举环为平台 seam 未单测——断言零删除）。
- 覆盖率口径：本地干净并集复算（4 个新鲜 cobertura，不含根 `TestResults/` 陈旧卷——混入会把分母抬高，复算时已剔除；CI 在干净检出上同口径，容差 0.5pp 兜底 4 行本地偏高）。
- 门禁：code-health、compose-root（C3 6/6，未新增根构造）、registration-discipline（R1b 9/11）、code-conventions、ui-copy（`×` 非 CJK，`…`先例）、increment-discipline（508/62/9/0，全帽内）、review-tier（LIGHT）全绿；其余（adr-format/md-links/cookbook/doc-budgets/readme-badges）同批全绿，见评审简报门禁自证；`dotnet format --verify-no-changes --severity warn` 干净。
- 实机未验：P1–P3 复验转用户下次插件更新重启（待办指针）。
