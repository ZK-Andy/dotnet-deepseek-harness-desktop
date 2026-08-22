# Agent Note: desktop-shell-self-update

Status: implemented

## Problem

桌面壳需要自更新。内置 Node+dsh 运行时打在只读安装区，「内核自愈」不可行（写权限/所有权/与崩溃监督耦合），只做壳自身更新。需求边界（用户定）：启动后台检查一次 + 手动检查入口；**不轮询、不发 toast**；下载包持久化「ready」跨重启提示；UI 仅 ready 态可见——hover 显示版本、点击直接装+重启。

## Decision

C# 胖后端 + 伴生插件瘦 UI：

- `Services/Update/` 状态机移植 opencode `updater-controller`（`idle→checking→downloading→ready→installing` + up-to-date/error）：检查/下载/安装全部委托注入 + ready 持久化接口，纯逻辑 xunit 覆盖；启动对账（ready 版本==当前版本→清除记录）后自动检查一次；并发检查去重；失败转 error 不阻塞后续重查。
- Feed 绕限流（hairyf 同款）：`releases.atom` 取最新稳定 tag（含 `-` 的预发布跳过）+ `expanded_assets/<tag>` 页抓资产 href；`ReleaseMeta.Pick` 按 RID 后缀挑资产（`_linux-amd64.deb`/`_linux-arm64.deb`/`_windows-x64-setup.exe`/`_macos-*.dmg`），相对 href 归一化。
- 下载 `.part` 临时名 + 完成原子改名 + SHA256SUMS.txt 强校验（双空格/`*` 二进制格式都解析），落地 `DSH_HOME/updates/`。
- 安装执行器派生分离进程即返回：Linux 写等待脚本（轮询本进程退出→`pkexec dpkg -i|rpm -U`→nohup 拉起新版同路径二进制）；Windows Inno Setup `/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`；macOS v1 抛 PlatformNotSupportedException 转 Error。
- UI 挂伴生插件：`sidebar.footer.action` slot（侧边栏底部设置入口上方动作行，用户选定位置；右下角 overlay 因小屏与输入框重叠被否）。组件仅 status=ready 渲染圆形下载钮（opencode 同款 hover 宽度展开显示「更新 vX.Y.Z」，rail 态纯图标 title 提示），点击 `ryn.invoke('desktop.update.install')`，installing 转圈禁点。
- 状态通道：宿主订阅状态机 transition → `EvaluateJavaScriptAsync` 派发 `dsh-desktop-update` CustomEvent；插件挂载时先 `ryn.invoke('desktop.update.getState')` 对齐初值。
- 参数归位：仓库/超时/目录进 appsettings.json `Update` 节（AOT 安全的手工 JSON 解析）；当前版本单一来源 csproj `<Version>`（发布 CI 以 `-p:Version=<tag 去前缀>` 覆盖）。
- 命令面：`desktop.update.getState/check/install`（`DesktopUpdateCommandRouter`）；check 立即返回当前态（下载分钟级不走 IPC 长等待），后续靠事件推送。
- **能力白名单**：`ryn.json` 的 `capabilities` 是命令命名空间开关（Ryn.Ipc `RynCapabilities`/`RynCommandDeniedException`，未声明即拒）——新增 ``desktop": true` 后 `desktop.update.*` 才可从页面调用；实测漏配表现为 invoke 500 + Command failed，页面侧无任何插件报错。

## Alternatives considered

- 右下角 shell.overlay 圆点 pill：落败——实机反馈小屏时与输入框重叠；且 overlay 属悬浮层不如侧栏动作位贴合导航结构。
- 更新按钮与设置按钮同排右侧：暂缓——现版壳该行为 single 座位，需 pnpm patch 内置闭包或上游 PR 才能实现；先用一等 list 插槽验证端到端机制，视觉不满意再走上游贡献。
- api.github.com 直查 latest：落败——匿名 60 次/h 限流，多客户端场景必踩。
- 轮询 + toast（hairyf/opencode 默认行为）：用户明确否决——启动检查一次即可，toast 只允许手动路径（v1 未做手动入口 UI，settings.section 区块留待后续迭代）。
- macOS 静默安装：v1 不做——无签名 + Gatekeeper 使静默链路不可靠，明确报错引导手动下载 dmg。

## Consequences

- 收益：ready 前完全静默零打扰；ready 持久化跨重启不丢「可安装」；状态机与 IO 全解耦可测（23 个新单测覆盖比较/挑选/校验/状态迁移/失败回退）；升级体验一键完成。
- 代价/风险：Linux 每次升级弹一次 pkexec 授权框；SHA256SUMS 缺条目时 fail loud 拒装（宁可误报不装坏包）；`sidebar.footer.action` 为上游非契约表面，靠逐 release 钉死内置 dsh 兜底；插件升版的版本感知重装仍未做（见 companion-plugin ADR，随 settings 手动入口一起排期）；发布流水线尚未接 `-p:Version` 覆盖（tag 出包前必须补，否则比较基准停在 csproj 默认值）。
- 验证：`dotnet test` 64→87/87 全绿；三部门禁全绿；0 警告；沙箱 dsh web 启动 + boot manifest 含 companion 行 + 新 client.js 伺服确认。真实桌面「有新版 release 时侧栏出现更新钮→点击静默装并重启」待实机验收。
