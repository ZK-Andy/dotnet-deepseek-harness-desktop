# Agent Note: self-update-prune-consumed-packages（自更新安装包用后即清 + 启动对账清陈旧包）

Status: implemented

Review: FULL/2026-09-04/R1=ok R2=ok R3=ok

## Problem

自更新下载的安装包（rpm/deb/exe）**安装成功后永久残留** `<DSH_HOME>/updates/`：代码无任何「安装完成即删包」路径——`InstallerDownloader.DownloadAsync` 只清 `.part` 半成品；`UpdateStateMachine` 的 `ClearAsync` 只清 `ready.json` 记录不清 rpm。`UpdateInstaller.LaunchAsync` 派生安装后本进程即退，也不删包。

2026-09-04 实机盘点（v0.4.4→v0.4.5 自更新后）：updates 目录累积 **16 个 rpm 共 ~1.5GB**（0.3.1–0.3.12 十二个 ~120MB 捆绑闭包时代包 + 0.4.x 四个 ~38MB online-first 包）。安装包唯一用途是「下载完→待用户点安装」的暂存：安装成功（版本进系统 + 重启对账 UpToDate）后，feed 按版本号比对、ReadyRecord 对账版本不高于当前即清记录，**没有任何代码路径回头读历史 rpm**——纯死重。旧形态包（0.3.x 捆绑闭包）更无保留价值。另有 `.download.lock` 死残留（0 字节、锁随进程死亡已释放但文件不删）与 `install.sh` 废弃形态残留（`BuildLinuxScript` 注释已证 argv 内联取代落盘，root 不读用户可写文件）长期无人清理。

## Decision

**用后即清 + 启动对账兜底**，双层覆盖：

1. **Linux 安装脚本装完删当前包**（主流路径）：`BuildLinuxScript` 生成的 root 脚本在 `rpm/dpkg` 成功后（`install exit=0`）删除当前安装包。删除在 root 上下文、包已进系统后执行——脚本已含 hash 复验与 PATH 收紧（ADR `self-update-pkexec-toctou` 防线），此时删包无 TOCTOU 增面，且装包成功即系统持有副本，删用户空间暂存无回滚损失。装包失败不删（保留供重试/诊断）。
2. **启动对账清陈旧包**（跨平台兜底 + 治存量）：清理 updates 目录中「版本 < 当前版本」的安装包（文件名解析版本 → `UpdateVersion.Compare`）。覆盖三面：Windows（Inno `/RESTARTAPPLICATIONS` 直接拉起新版、无 root 脚本接管，删包只能靠新版启动对账）、Linux 历史存量与「ready 残留/安装中断」路径、macOS 手动更新残留。只删**严格旧于当前**的包——「当前版本 + 最新待装」保留（回滚/重装余量）。

清理落点是**组合根 `LoadUpdateMachine`（`DesktopBootstrap.Lifecycle.cs`）内、状态机启动对账前**的同步调用（`StalePackagePruner.Run`），进 updates 目录执行：启动即对账，不新增独立后台任务与生命周期接线；**刻意不放进状态机 `StartAsync`**——清扫是 IO 副作用，状态机保持「检查/下载/安装」纯逻辑可单测，目录删除属组合根装配副作用（与 InstallCompanionBeforeSpawn 等同类）。纯删除判据（`SelectStale`）仍纯函数可单测（注入目录/版本比较）。**不新增可调参数**——清理策略固定（旧于当前即删），不为它开配置面。

启动对账删除与 Linux 脚本删除重叠无冲突：Linux 装完即删的包（=刚装的版本），下次启动时它已 ≤ 当前，本就在清扫范围内（文件已不存在则删除幂等 no-op）。

**附带清理**：`.download.lock` 死文件（0 字节残留，下载锁随 FileStream 释放已死；下次下载 OpenOrCreate 重建，删之无害）与 `install.sh` 废弃形态（argv 内联已取代，永不生成）。随对账一并清。

## Alternatives considered

- **只手动清一次（A 方案）不动代码**：立即回收但每两三个版本又攒 1GB，问题周期性复发。落败——治本需要产品逻辑（用户已拍板先 A 后 B）。
- **安装器脚本删包 + 只删「刚装的当前包」**：覆盖 Linux 主路径但不治 Windows 与历史存量——Windows 无 root 脚本接管（Inno 直拉新版）、存量 1.5GB 级残留仍要靠人工。落败——需启动对账兜底。
- **删包放在下载前（先清旧再下新）**：覆盖 Linux 正常流但漏「下载后未装即退出」的 ready 残留；且清理时机延后到下次下载才触发，不满足「启动即回收」。落败——对账时机更早更全。
- **updates 目录整体按「只留最新」裁剪（含 ready 待装包）**：ready 态「下载完未装」的包是待装目标，删了用户点安装会失败（InstallAsync 校验 AssetPath 存在）。落败——必须保 ready 指向包，故按版本而不是按数量裁。
- **保留最近 N 个版本**：回滚余量看似更足，但历史包从未被回滚路径引用（无版本回滚 UI/机制），保留即死重；且 N 的取值又要开配置面。落败——只保「当前 + 待装」足够。
- **删包落用户空间（新版启动时自删）**：新版启动即删自己的安装包，但安装包在用户空间、用户可写，启动删「旧于当前」包无安全增益，且删包动作要等新版完整启动后才发生（比 root 脚本晚一拍）。落败——root 脚本装完即删最干净，启动对账只作兜底。

## Risks

- 脚本删包失败（权限/IO）不阻断安装主链路——删除包在 `install exit=$?` 判定后，失败仅留日志（`fail loud` 到 install.log），下次启动对账兜底再删。
- 对账清扫误删「正在被另一实例使用」的包（并发下载中）——清扫只删「旧于当前」且非 `.part` 的整包；`.part` 半成品由下载器自己的异常路径清。对账在**下载锁 `.download.lock` 被他实例持有**时整段跳过（防与在途下载竞态；死锁文件——无持有者——照常清扫删除，下次下载重建）。
- 版本从文件名解析失败（异常命名/历史脏文件）——解析失败的文件不删（保守跳过），不因清扫引入误删。
- 清扫本身是增强、非启动前提：`StalePackagePruner.Run` 整体收拢 IO/授权异常记日志（fail loud），绝不打其所在组合根启动路径。

## Consequences

- **行为**：Linux 自更新装包成功后 updates 目录不再残留刚装包（install.log/ready.json 除外）；启动对账删「版本 < 当前」包与 .download.lock/install.sh 残留，当前版本与 ready 指向包保留。
- **边界**：`StalePackagePruner.Run` 整体收拢 IO/授权异常记日志（fail loud），清扫是增强、绝不打其所在组合根启动路径（评审 R2 Blocker 修正确认）。
- **风险残留**：删除动作无回滚（包已进系统或已弃，无版本回滚路径引用）；跨实例下载锁竞争窗口在单实例仲裁下不可达（残余理论窗口，记留）。
- **测试**：`StalePackagePrunerTests`（TryExtractVersion/SelectStale 纯判据 + Run 目录行为）+ `UpdateInstallerTests`（脚本删包成功守卫）；`dotnet test` 493/493、format/code-health/conventions/adr-format 全绿。

## Related

- [desktop-shell-self-update](../process/2026-08-22-desktop-shell-self-update.md)（implemented）：自更新状态机机制本尊；本次只加「用后即清」副作用，不动状态机流转。
- [self-update-pkexec-toctou](../bug-fix/2026-08-28-self-update-pkexec-toctou.md)（implemented）：安装脚本 TOCTOU 防线（argv 内联/hash 复验/PATH 收紧）；删包动作接在其防线之后，不新增信任面。
- [shell-observability-diagnostics](../architecture/2026-08-24-shell-observability-diagnostics.md)（implemented）：HostLog/run-marker 诊断家族；updates 残留的观测（本次实证 1.5GB）由此暴露。
- [self-update-exit-reaps-dsh-child](../bug-fix/2026-08-28-self-update-exit-reaps-dsh-child.md)（implemented）：自更新退出收割 dsh 的相邻生命周期收口；本次收口对象是安装包文件残留。
- [review-brief-gate-self-assertion](../process/2026-09-04-review-brief-gate-self-assertion.md)（implemented，同批）：本批 FULL 三审暴露「主会话漏跑 format 却写已盖」触发简报门禁自证机制；本 ADR 是其实证来源。
