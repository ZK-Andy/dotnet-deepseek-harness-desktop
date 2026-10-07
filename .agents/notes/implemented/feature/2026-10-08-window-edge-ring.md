# Agent Note: 窗口边缘描边环

Status: implemented

## Problem

本壳三端统一 `TitleBarStyle.Frameless` + 不透明窗，合成器侧不给无装饰窗口补阴影【推断 · 未证：机制面在合成器/窗口系统，现象面是用户实机反馈】。浅色主题下白内容落在背后同样白色的其他软件界面上时，窗口边界不可辨——不是边框难看，是边框不存在。深色主题天然有对比度，不受影响（用户实机截图佐证：深色窗口落在浅色背景上边缘自明）。

## Decision

**视口内侧 1px 发丝线描边环**（`PageBridge.CaptionBar`）：与 ZCode 的透明窗 + 内层圆角面板不同，本壳不切透明窗，只加一层纯装饰描边。

- 元素：`#dsh-desktop-edge-ring`（`EdgeRingId` 公开常量，供测试引用），`position:fixed;inset:0`，独立幂等守卫（id 与顶栏不同，存在性各判各的——顶栏守卫命中即整段返回）。
- 颜色：`box-shadow:inset 0 0 0 1px var(--dsw-alias-border-l3,rgba(127,127,127,.4))`——借 dsh 自有分割线 token（侧栏 `border-left` 同源），明暗主题自动跟；token 缺失回退中灰，双主题都可见。
- 事件：`pointer-events:none`，且是全注入脚本**唯一**允许该声明的规则——环贴在视口四边，可命中即吞掉无边框窗口的边缘 resize 与边缘点击。
- 层叠：`z-index:2147483646`，紧贴顶栏（2147483647）之下、浮于模态遮罩之上——窗口边缘任何时候可见。
- 范围：与平台无关，三端同款（macChrome 闸内外都带环）；展开/折叠/全屏不区分——全屏隐环与全屏隐灯同一批 gate（见 Deferred）。

## Alternatives considered

- **指望合成器阴影**：Mutter 等只给有装饰窗口画阴影，无边框 frameless 无此待遇；Ryn 侧也没有开阴影的口子（ZCode 反而主动 `hasShadow: false` 踢掉某些 WM 的黑边）。等于等上游，不掌握在自己手里。落败。
- **学 ZCode 内圆角面板（透明窗 + renderer 内画 12px 圆角）**：要切透明窗、重验三端材质与拖拽，是大手术；且圆角解决的是"方的还是圆的"，白 on 白下一样 invisible——描边该加还得加。不对症，落败。
- **固定颜色描边（如纯灰/纯黑）**：浅色够用、深色扎眼（或反之）。dsh 自有 token 双主题自适应，无需两套规则。落败。
- **给 `html/body` 加 `border`**：改盒模型，1px 会吃掉布局/触发滚动条；`inset` 盒阴影画在背景盒内侧，无布局副作用。落败。

## Consequences

- 买到的：浅色白 on 白时 1px 边界；深色零变化（token 自适应）；无窗口级改动，无透明窗材质风险。
- 付出的：借用 dsh 私有 token `--dsw-alias-border-l3`——上游改名时环静默消失（失效形态是"没圈"，不崩）；`pointer-events:none` 零容忍测试收窄为"全脚本唯一一处"（注入条可命中不变量改由 occurrence 计数守卫）。
- 全屏态下环仍显示（与自绘灯同状），待全屏隐灯一批 gate（ADR frameless-uniform-caption-bar 的 `html[data-fullscreen]` Deferred）。

## Testing

- `CaptionBarTests.Build_DrawsWindowEdgeRing`：规则几何 + token 前缀 + z-index + 独立守卫 + occurrence==1 + 闸内分支带环。
- `Build_DrawsMacTrafficLights` / `Build_MacChrome_GreenButtonEntersNativeFullscreen`：`DoesNotContain("pointer-events:none")` 改为 `CountOccurrences(...) == 1`——注入条可命中不变量不变，唯一合法出处钉为描边环。
- 实机验收：Linux 浅色白 on 白边界可见、深色无变化、三键/拖拽/边缘 resize 不受影响（合成器抓取只能手验）。

## Related

- [frameless-uniform-caption-bar](2026-10-07-frameless-uniform-caption-bar.md)——无边框自绘顶栏总纲；全屏隐灯 Deferred 与本环的全屏 gate 同批。
