# Agent Note: 窗口 chrome 去补丁化——Core 端口化 + 渲染与传输解耦

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`PageBridge/CaptionBar.cs` 把三个外部世界的知识写进同一个字符串拼接函数：Ryn 原生（`data-webview-*`、`window.startDrag/beginNativeDrag/setFullscreen` IPC）、Web 文档（DOM 结构、CSS 盒模型、`html[lang]`）、dsh 上游（`--dsh-frame-*` 协议变量、`sidebarCol/logoRow/toggle` 哈希类、`headerLeading` 簇）。同一 `return` 干六件事：变量发布、侧栏让位、折叠按钮位移、红绿灯几何、毛玻璃链、拖拽状态机。上游真正的契约是三个变量（`AppFrame.module.css` + `Modal.module.css` + `overlay-top-margin.ts`），本壳只发两个——`overlay-top` 缺失使高卡片顶边距回落到 24px（应为台阶 + 20px）。

## Decision

`CaptionBar` 拆成三层各归其位（行为除补上 `overlay-top` 外零变化）：

1. **Core 拥有域模型与端口**：`Core/WindowChrome/ChromeInsets.cs`（值对象：`TopClearancePx/OverlayTopPx/ChromeTopPx` + `OverlayTopExtraPx = 20` + 纯函数 `Resolve(heightPx, fullscreen)`）与 `IWindowChromePolicy.cs`（端口 + `WindowChromePolicy.Default` 共享默认实例，`CaptionBarOptions.Default` 同型）。全屏映射完整建模（全屏只留 20px 间隙），调用方恒传 `false`——本壳无全屏状态源，`data-fullscreen` 静态不发。
2. **`CaptionBar` 退化成薄渲染器**：`BuildCss` 拆成 `BuildChromeVariables/BuildSidebarClearance/BuildToggleReturn/BuildTrafficLights/BuildVibrancyChain` 五个命名单元，只拼装不计算；协议名收敛为四个 `private const`（协议改名只改一处）；带上界取自 `insets.TopClearancePx`（值恒等于 `HeightPx`）。拖拽 JS 字节不变——删除主张在实现前订正中落败（见 Alternatives）。
3. **三变量发布**：`:root` 段新增 `--dsh-frame-overlay-top`（模型值字面发布，渲染不重算）；`chrome-top` 恒 0 不变。
4. **测试钉契约**：`Core.Tests/WindowChrome/ChromeInsetsTests`（台阶/定位/留空映射、全屏分支、端口同解）6 例；`CaptionBarTests` 同一 Theory 加钉 overlay-top 模型值 + `data-fullscreen` 静态不发（默认与 mac 双路）。新命名空间进四个 csproj 的全局 `<Using>` 清单（层内惯例）。
5. **中线（未做，分期）**：按 `anywhere-labs/dsh-desktop` 模式走插件槽（`shell.leading`/`shell.overlay`），哈希类依赖一次清零——待真机可验时另立项。

## Alternatives considered

- **继续在 `CaptionBar` 里加分支（落败）**：过去三折证明每加一支都是下一次漂移的本金；`overlay-top` 缺失这类"修一半"重复出现。
- **`chrome-top` 改不发布、靠 fallback 0px（备选，未采）**：与上游 darwin 语义字面一致；但显式 `0px` 与现有测试钉（`--dsh-frame-chrome-top:0px`）及 `fb14241` 结论一致，不值得再翻一次。显式发布无 `!important`、与上游 Windows 分支拼注入顺序的风险如实记录，见 Consequences。
- **删除拖拽 JS、双击还给原生（落败·实现前订正）**：与已落地的 `frameless-uniform-caption-bar` 实证直接冲突——`data-webview-drag` 全宽覆盖已实机落败（按钮点击被吞）；Ryn 自动拖拽条对自顶带起算的 chrome 按内容处理（判据 `bottom ≤ strip × 1.5` 不可靠）；DOM `dblclick` 在顶带不可达（合成器隐式抓取吞序列），删自判即丢双击缩放。结构性搬移（换主人）已做，行为删除留待有真机矩阵时重验。
- **静态发 `data-fullscreen`（落败·实现前订正）**：它是运行时状态，静态发即对上游 CSS 说谎（上游全屏分支靠该属性切换 overlay/chrome-top）；本壳无状态源，不发。
- **自绘灯整体改原生（不可行）**：Ryn 无 `hiddenInset/vibrancy` 原语，Linux 无原生灯；灯体保留，几何还给声明式是远期方向。
- **一次性重写页面层（落败）**：风险与收益不成比例；本篇五步除第 5 步中线外全部合入、行为除 overlay-top 外零变化。

## Consequences

- 买到的：dsh 改类名只改渲染器，改变量语义只改 Core 端口，换 Ryn 版本只改传输适配器；`BuildCss` 从单 `return` 六职责变为五命名单元 + 总装一行。
- 付出的：`overlay-top` 补上后高卡片顶边距从 24px 变为台阶 + 20px（52→72），属纠正性行为变化，需一次实机目视（顶带验收面）；显式 `0px` 的注入顺序假设未变，仍随上游 Windows 分支行为。
- 测试：1031/1031（基线 1025 + 本篇 6 例），本地覆盖率 63.77%（基线 63.59%，CI 口径为准，覆盖率维持）。
- 与组合根机制 ADR 的关系：本篇的 `IWindowChromePolicy` 是那篇用例下沉 Core 的第一个住户；`WindowChromePolicy.Default` 是容器注入就位前的过渡形态（组合根 new 配额零消耗），注入化随那篇收官。
