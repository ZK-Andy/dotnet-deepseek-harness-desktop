# Agent Note: 应用重启动作（托盘 + 桌面设置）与开关对齐 DSH 原生 Switch

Status: implemented

Review: FULL/2026-10-05/R1=ok R2=ok R3=ok

## Problem

三个用户可感缺口：

1. **没有重启入口**。壳的退出路径只有托盘「退出」与关窗；想干净重启（改完偏好、升级 dsh、排查异常）只能手动退出再从 launcher 拉起。更关键的死角：页面→壳 IPC 通道失效（端口漂移/origin 变化，ADR port-drift-ipc-origin-mismatch 家族）时，设置页反复提示「重启应用可恢复」却给不出动作——而托盘原生菜单恰是通道失效时唯一存活的 UI 面。
2. **无托盘环境没有兜底面**。Linux 无 AppIndicator 扩展的 GNOME 没有系统托盘（`closeToTray` 的 `available=false` 分支即证据）；托盘是重启的**主位**但不是**唯一位**，设置页必须补上无托盘环境下的重启入口。
3. **设置页开关样式不一致**。「桌面」区块的开关是自绘 opencode 同款 28×16 方角克隆，与 DSH 官方 UI 的开关观感不同。

## Decision

**重启动作**（一个宿主动作、两个入口）：

- **拉起新实例的机制**：Infrastructure 新增 `AppRelaunch.SpawnSelf()`——按 `Environment.ProcessPath` + 原始命令行参数（`GetCommandLineArgs().Skip(1)`，保留 dev 隔离开关）派生自身，进程分离（不等待）。子进程继承当前会话作用域与环境（DISPLAY/DSH_HOME 等），不做 systemd-run 包装——本路径不经 pkexec/runuser，作用域天然继承。
- **退出编排**：`ExitPipeline` 新增 `Restart(Action spawnSuccessor)`，与 `OrderlyQuit` 共享同一 once-guard，次序为**回收三件套 → 释放单实例监听器 → 拉起新实例 → 关窗 → 看门狗**。回收先于 spawn 是硬约束：旧 dsh 树必须在 新实例 spawn 前死透，否则看门狗的 `stopHost` 可能击杀新实例的 dsh；监听器释放先于 spawn，新实例才能完成单实例仲裁（marker 已在回收步释放）。
- **宿主命令**：`desktop.app.restart`（`AppRestartCommandRouter`）。受理即回 `{}`，实际重启延迟 200ms 执行——给 IPC 响应留冲刷窗口，页面不至于把「已受理」等成超时；延迟是协议常量非可调参数。执行时先 `CloseGate.ApproveExit()` 再走 `ExitPipeline.Restart`（与托盘退出同一先批准再关窗契约）。
- **托盘入口**：菜单在分隔线后加「重启」（`重启`/`Restart`），位次：显示主窗 /［检查更新］/ ─ / 重启 / 退出。双语随 `UiCopy.TrayRestart`，语言切换重建走既有 `RebuildMenu` 链路（ADR host-ui-locale），companion 中继是哑中继无需改动。
- **设置页入口**：「桌面」区块尾部加「重启应用」行（按钮 + 失败转页内错误提示）。通道失效时该按钮与其它命令一样发不出——`hostUnreachableDesc` 文案同步改为指向托盘菜单重启。通道正常但响应未达（重启先于冲刷）按失败显示，页面随进程退出销毁，实观感不受影响。
- **开关对齐**：运行时 `require('@deepseek-ai/dsh-client-ui-primitives')`，其导出含官方 `Switch`（36×20 胶囊、`--dsw-alias-brand-primary` 选中色、必须 `label` 作 aria 名）则直接消费；导出缺失（当前捆绑的 0.1.1-rc.2 即无 Switch 导出，上游 2026-09 树已有）则回退到按官方 `Switch.module.css` 规格逐项重画的克隆（`.ddc-sw`），装上含 Switch 导出的 dsh 后自动切换为原生组件。

## Alternatives considered

- **重启只放托盘、不动设置页（落败）**：少一处 UI。落败原因：无托盘环境（Linux GNOME 裸装）下托盘不存在，重启入口归零；且 IPC 通道失效场景中设置页的「请重启」提示指向托盘后仍需要一个「通道正常时」的页内动作闭环。
- **开机自启/关闭到托盘同步进托盘菜单（落败）**：Ryn `TrayMenuItem` 无开关形态，只能「标签翻字」表达状态；两项均为「设一次忘一年」的低频配置，入托盘要为它们新建第三份状态同步面（偏好文件 + 系统注册态 + 托盘标签），且拉长逃生菜单。状态展示与降级处理（重试/回滚/无托盘禁用）已由设置页闭环，无重复价值。
- **复用 `desktop.update.install` 的重启路径（落败）**：该路径的「重启」由安装脚本在包管理器装完后拉起，绑定「有 ready 待装包」前提；裸重启没有安装包与授权观察窗，硬凑会把两件事耦死在状态机上。
- **spawn 延迟到进程退出后（落败）**：如借 shell `while kill -0 $PID` 等待环（自更新脚本同款）。落败原因：多一层 shell 派生与日志落位，而 `ExitPipeline.Restart` 的次序（回收+释放先于 spawn）已消除唯一竞争（旧实例仲裁/marker 占位），等待环买不到额外正确性。
- **克隆开关只调 CSS 不做原生探测（落败）**：省一次 require。落败原因：dsh 升级带出官方 Switch 后克隆会重新漂移；探测一行 try/require 成本极低，原生可用即零漂移。

## Consequences

- 买到的：通道失效场景有原生兜底动作；无托盘环境有页内重启；「改完设置要重启」一类指引从建议变成按钮；开关随 dsh 升级自动收敛到官方组件。
- 付出的：`ExitPipeline` 多一条路径与 once-guard 交互面（回归钉住）；重启竞态窗口（spawn 与旧进程退出重叠约百毫秒）依赖「回收先于 spawn」次序不变——该次序由测试钉死；重叠窗口内新旧两进程并发写 host.log，`HostLog` 的文件闸与轮转是进程内的，最坏丢/绞一行日志（异常已有命名留痕，量级极低，评审 R2 留档）；克隆开关在无 `--dsw-alias-brand-primary` 令牌的极老 dsh 上按回退色渲染（防御性兜底，与既有令牌用法一致）。
- `desktop.app.restart` 是新协议面：旧 companion 页面对新宿主无感（按钮发了命令宿主不识别 → 错误帧 → 页内提示），新宿主 + 旧页面（无重启行）也不受影响，无版本耦合。

## Related

- [shell-tray-hide-to-tray](../architecture/2026-08-24-shell-tray-hide-to-tray.md)——托盘事件中继与关窗闸门先批准再关窗契约；重启路径复用同一闸门。
- [host-ui-locale](2026-08-28-host-ui-locale.md)——托盘菜单双语与语言切换重建机制。
- [companion-settings-consolidation](../bug-fix/2026-08-24-companion-settings-consolidation.md)——「桌面设置」单页三区块的既有拍板；重启行并入「桌面」区块而非新开页。
- [port-drift-ipc-origin-mismatch](../bug-fix/2026-09-12-port-drift-ipc-origin-mismatch.md)——通道失效场景的处置家族；重启入口是其用户侧兜底。
