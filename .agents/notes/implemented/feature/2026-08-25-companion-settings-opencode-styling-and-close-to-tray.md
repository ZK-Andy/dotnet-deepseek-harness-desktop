# Agent Note: companion-settings-opencode-styling-and-close-to-tray

Status: implemented

## Problem

「桌面设置」页两件事：①视觉与交互形态陈旧——按钮为自绘描边样式，开关语义用「启用/停用」按钮表达，与主流桌面端设置页的行式布局脱节；②「关闭即隐藏到托盘」是写死行为，用户没有退出通道的选择权——想点 X 直接退出的用户只能走托盘菜单绕路。

## Decision

1. **设置页整体切换原生 settings 区块形态**（结构参照 `anomalyco/opencode` settings-v2 的行式布局；视觉令牌对齐 dsh 原生 `settings-plugins/general` 页——规格自真实 DOM 的内联主题样式中提取）：分组标题 16px/600·lh1.5（首版 line-height:1 曾致 CJK 标题视觉截断）；卡片 = `bg-layer-3` 底 + 1px `border-l2` 描边 + 12px 圆角；行内「标题(13px/500)/描述(label-tertiary 12px) 居左、控件居右」，行间 border-l2 分隔。按钮为原生 save/discard 两态（主=反色填充，次=描边幽灵）；检查更新独立成行、标签随状态机切换（立即检查→检查中…→下载中…→安装并重启→安装中…），ready 态安装入口并入该按钮。布尔项为 Switch 克隆（28×16 轨道 + 14×14 滑块，checked 走 success-primary）。修正版以 headless Chrome + 真实主题令牌确定性验证：25 节点零截断、计算样式逐项命中、双跑字节级一致。
2. **新增「关闭时最小化到托盘」开关行**（文案按用户拍板原文）：宿主新增 `CloseBehaviorPreference`（持久化 `<DSH_HOME>/desktop-preferences.json`）+ `desktop.closeToTray.getState/set` 路由（帧含 `available` 标记托盘就绪与否，无托盘环境客户端禁用开关）。`Closing` 回调裁决改为：闸门放行通道优先，其次偏好为真才取消关窗转隐藏。

## Alternatives considered

- **仅换配色不动布局**：落败——问题不在颜色而在信息架构；行式布局是本次对齐的核心价值，半途而废下次还得重开一轮。
- **偏好存 appsettings.json**：落败——appsettings 是随包只读基线，运行时可变偏好写进去会被升级覆盖；DSH_HOME 是既定的 home 层数据位（与 credentials/storages 同层），且天然多实例共享。
- **默认值取 false（关闭即退出）**：落败——存量用户升级后行为突变（点了 X 结果应用没了）违背最小惊讶；true 与历史行为一致，升级零感知。
- **无托盘环境仍允许开启偏好**：落败——隐藏无从谈起，开关形同虚设还会误导；以 `available:false` 显式禁用并说明原因更诚实。

## Consequences

- companion version bump **0.0.13**（版本感知升级约定），随下版发版生效。
- 视觉令牌直接消费宿主运行时注入的 `--dsw-alias-*` 主题变量（theme 插件按用户主题注入），深浅色自动跟随；迷你宿主验证须先注入 theme 块否则变量悬空。
- ready 态安装入口从独立主按钮并入检查更新按钮：侧栏圆形更新钮不受影响，两条安装入口并存如前。
- 测试 252/252（+8：偏好持久化 4 例、路由契约 4 例）；client.js `node --check` 过。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：CloseGate 放行通道与本偏好的裁决关系。
- [companion 设置单页化](../bug-fix/2026-08-24-companion-settings-consolidation.md)：本页三块合一的前置形态。
- [随包插件版本感知目录](../feature/2026-08-25-bundled-plugin-version-aware-catalog.md)：version bump 触发升级的机制依据。
