# Agent Note: companion-settings-consolidation

Status: implemented

## Problem

v0.3.0 实机使用反馈两项信息架构问题：①companion 在设置页注册了三个独立 section（桌面设置 order 50 / 诊断 51 / 桌面 52）——dsh 每个 settings.section 都是一条独立导航项且图标统一回退齿轮（宿主白名单硬编码，插件无法自带图标），三口齿轮导航既是噪音又无辨识度；②旧 home 切换横幅每次启动都显示（无持久已读标记），对已了解切换事实的用户是常驻骚扰。

## Decision

1. **单一「桌面设置」页**：三块合一——更新块（版本行/状态/手动检查）、诊断导出块、开机自启开关同页纵向排布（`.ddc-page` 栈式布局，块间距 24px）；slot id 沿用 `dsh-desktop-companion-update` 不变。插件 version bump **0.0.8→0.0.9**。
2. **去除旧 home 界面横幅**：`LegacyHomeNotice` 收缩为只读探测面（`IsPresent`/`LegacyPrivateHome`），`BannerScript` 及其注入调用、堆叠偏移引用（RunMarker/UpdateBanner 脚本内的 `legacy-home-banner` 选择器）、纯脚本单测一并删除；检测事实仍留一行 host.log（本轮缺陷定位正依赖该日志）。版本底线横幅与非受控退出横幅不受影响。
3. 文档同步：user-guide 双语（诊断入口路径、升级段落去「一次性提示」表述）、architecture（启动期告知与设置页结构两处）。

## Alternatives considered

- **保留多页、给每块做图标**：落败——settings.section 契约无 icon 字段，差异化只能上游 PR（在案 Gotcha），成本远超收益；用户明确拍板合并。
- **横幅加持久已读标记（显示一次后不再出现）**：落败——用户直接选择完全去除；且「提醒直到处理完旧数据」的原始语义已被「日志留痕 + user-guide 升级段」覆盖，无需折中形态。
- **横幅降级为一次性 toast/通知**：落败——Ryn 通知能力未验证（在案风险），且同样被用户否决。

## Consequences

- 设置页导航从三条齿轮项收敛为一条；诊断导出与自启开关的入口路径变为 设置 →「桌面设置」（user-guide 已同步）。
- 旧 home 用户升级后不再收到任何界面告知——数据位置事实由 docs 承载；host.log 首行仍可取证（`检测到旧版桌面数据目录…`）。
- 0.0.9 经版本感知升级随下次发版自动替换用户 profile 内副本；0.0.8 从未随版发布，无存量用户。

## Related

- [invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：同日实机反馈批次的前半（功能失效修复），本笔记是其后的信息架构收敛。
- [共享 home 切换](../architecture/2026-08-23-shared-home-desktop-profile.md)：被去除横幅的原出处。
- [开机自启](../architecture/2026-08-24-shell-convenience-autostart-ready-notify.md)与[诊断可观测性](../architecture/2026-08-24-shell-observability-diagnostics.md)：两块并入单一页面承载。
