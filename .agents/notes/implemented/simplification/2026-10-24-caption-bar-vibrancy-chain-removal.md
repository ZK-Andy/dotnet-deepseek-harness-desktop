# Agent Note: 毛玻璃透明链死半边摘除——页面侧单边透明不再注入

Status: implemented

Review: FULL/2026-10-25/R1=ok R2=ok R3=ok

## Problem

Ryn/saucer 残留清账（[ryn-residue-cleanup](2026-10-24-ryn-residue-cleanup.md)）转出一条未核实项：`CaptionBar.cs` 生产注释宣称「窗口背景按平台清成透明」（毛玻璃 vibrancy 机制的窗口侧半边），但 src 无 `TransparencyLevelHint` 落点。本批核实结论：**窗口侧半边不存在，页侧透明链是死半边**——

- 机制原貌（frameless-uniform-caption-bar ADR「macOS 毛玻璃」条）：两半同源 `MacNativeChrome` 闸——窗口侧旧 Ryn 宿主 `RynOptions.Backdrop = Blur`（Ryn 把窗/网页背景清透明、`NSVisualEffectView` 垫材质）+ 页面侧注入链（`html,body` 透明 + 侧栏列 80% `color-mix`）。G5 下线旧宿主时窗口侧半边随之消亡，页侧半边静默留存。
- Avalonia 宿主（`ShellWindow`）从未设 `TransparencyLevelHint`/透明背景，窗口不透明。
- 当前 `Avalonia.Controls.WebView` 12.1.1 包的 `NativeWebView` 控件无背景/透明度公开 API（XML doc 全量核对：仅弹窗类型 `NativeWebDialog.DefaultBackground` 有背景旋钮）——即使窗口开 acrylic，盖满全窗的原生 WKWebView 自带不透明底也把它挡死。
- 净效果【推断 · 未证：mac 观感无观察面——仓内口径该特性从未被真机/截图验证，无 run 佐证】（mac 闸内）：`html,body{background:transparent!important}` 透出的是 WebView 默认底、侧栏列被洗成 80% 白底近似色，既非官方 vibrancy 也不复 dsh 自有侧栏填充。摘除判据不依赖此条——「窗口侧半边不存在 + 包无旋钮」两条已证事实已足，本条只描述为何页侧单边无收益。

## Decision

页侧死半边整体摘除：`CaptionBar.BuildVibrancyChain` 删除，`BuildCss` 去掉 `macChrome` 追加支（签名收窄）；`MacNativeChrome` 类注从「窗口选项（装配面）与注入脚本两侧同源」改现行态——判据现只剩绿灯原生全屏语义一个消费点，保留独立类型是为闸界真值表回归钉与降级理由留家。`CaptionBarTests` 的透明链断言反转为防回潮钉（`Build_MacChrome_OmitsVibrancyTransparencyChain`：mac 闸内与非 mac 两分支都不得产出 `background:transparent`/`color-mix(`）。mac 观感回到 dsh 自有渲染（侧栏填充色全覆盖），与 Linux/Windows 同源。

## Alternatives considered

- **在 Avalonia 宿主补窗口侧透明（`TransparencyLevelHint` + 透明背景）**：落败——包无 webview 背景旋钮（见 Problem），效果为零；绕过包戳 WKWebView 私有 API（`drawsBackground`）属 Ryn Rosetta segfault（run `37545883056`）同族的原生手艺，且 mac 面无真机、CI 不启应用，未验证即上线违反证据纪律。将来包若暴露透明旋钮，须连同防回潮钉一起换成两半同源断言再重引。
- **只改注释、保留死链**：落败——「注入一条无 backend 的透明 CSS」是死语义面，与 ryn-residue-cleanup 的摘除判例同一性质；留着的唯一效果是 mac 侧栏洗白，无人能为之辩护。

## Consequences

- mac（闸内 arm64/Intel）侧栏观感相对迁移期发生变化【推断 · 未证，同 Problem 净效果条】：从 80% 白底近似色回到 dsh 自有填充色。该面本就压在「macOS 观感永不真机验证」的已知偏离里（frameless-uniform-caption-bar ADR 专项销账条），CI 不崩门不受影响。
- `MacNativeChrome` 的命名（原生 chrome 增强）比现行职责（绿灯语义单点）宽——不改名，改名触发 ShellWiring/桥链三处连带与闸界历史指针失效，收益不抵。
- [ryn-residue-cleanup](2026-10-24-ryn-residue-cleanup.md) Consequences 的未核实项随之关账。

## Testing

- `Build_MacChrome_OmitsVibrancyTransparencyChain`（反转钉）+ 既有 mac 闸内/闸外用例全绿；全量 `dotnet test` 回归。
