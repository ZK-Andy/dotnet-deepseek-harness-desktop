# Agent Note: Ryn 0.38.0 bump 与壳侧 AuthorizeIpcOrigin 接入

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

Ryn #91（受信 origin 集合 + `IRynWindow.AuthorizeIpcOrigin`，进 v0.36.0）与 #92（Windows `saucer_icon_new_from_file` 0xC0000005，随 v0.38.0 PR #95 修复）合并发版后，壳需要跟版：四包 0.35.1 → 0.38.0，并在受信 origin 新语义下接线——0.38 起**未授权 origin 的页面 IPC 被拒（token 有效也拒）**，而壳有两处导航到「非初始受信 origin」的路径：引导完成从 `ryn://app` 占位页进 dsh 主界面（`opts.Url` 为 null，dsh origin 从未入受信集）、监督器崩溃重启/交接后导航到可能漂移的新端口。

## Decision

- **四包 bump 0.38.0**（`Ryn`/`Ryn.Plugins.Tray`/`Ryn.Callbacks`/`Ryn.Ipc.Generator`）：无 API 破坏，0 警告。
- **新增 `AuthorizeIpcOriginFor(Uri)`**（组合根私有）：`GetLeftPart(Authority)` 派生 origin → `IRynWindow.AuthorizeIpcOrigin`，留痕日志，fail loud（窗口未就绪异常照抛；两处调用面均已在窗口就绪后）。
- **接线两处**（导航前授权，bridge 随下一次导航安装）：
  - 引导完成两跳：第一跳前授权裸 origin（同第二跳 origin，幂等覆盖两跳）。
  - 监督器 `navigate` 回调：`_webUrl` 刷新后、`NavigateAsync` 前授权新 origin——崩溃重启换端口（漂移）即被覆盖。
- **冷启动 `opts.Url` 路径不加显式授权**：Ryn 0.38 自动信任初始 `RynOptions.Url` origin（沙箱实测：首选端口被占 → 漂移 43535→46533 → 页面健康 alive、无 IPC 拒绝记录）。

沙箱三场景复验（debug 构建 + 隔离 HOME，2026-09-14）：①首启引导（真装 dsh）授权+两跳+直达主界面；②kill -9 dsh 两次，监督器恢复导航均带授权、页面健康 alive；③冷启动占住首选端口强制造真漂移，新 origin 正常服务。612/612 测试绿。

## Alternatives considered

- **不做壳侧接线（维持 0.35.1 或仅 bump 不授权）**：落败：0.38 语义下引导完成与漂移两条路径的页面命令通道必然被拒（占位页/新端口 origin 不在受信集），等于功能回归；这正是 #91 设计要堵的「未受信 origin 带 token 说话」。
- **把授权下沉进 `RynNavigationCallbacks.OnWebViewNavigating`（导航层统一授权一切 http origin）**：落败：等于授权一切经过的 origin，架空受信集合；壳侧授权点只有两个、且都明确是「宿主主动导航到自管 dsh」，收窄在组合根更贴合 #91 的威胁模型。
- **等上游提供「漂移后窗口重建」接口再一并处理**：落败：`AuthorizeIpcOrigin` 已是上游给出的正式机制，本次接入即消掉该等待面（见 [drift-command-channel-selfheal](../../rejected/bug-fix/2026-09-12-drift-command-channel-selfheal.md) 的 rejected 收口）。

## Consequences

- 收益：跟版上游安全语义；漂移后命令通道免重启恢复（原「重启应用可恢复」的用户代价消失）；Windows 打包探针随 #92 修复复绿（bump 后首次 package-windows 实跑确认）。
- 代价：未来任何「壳主动导航到新 origin」的新路径都必须先授权（评审检查点：R3 面新增导航点时核对 `AuthorizeIpcOriginFor` 前置）。
- 边界：授权无撤销调用面（上游暂未暴露 revoke），壳场景 origin 单调增加且均为自管 loopback，无越权面。
