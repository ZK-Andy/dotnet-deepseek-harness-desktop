# Agent Note: 壳 boot 页对齐 dsh 设计系统——回退调色板与墨色单色系

Status: implemented

Review: LIGHT/2026-09-30#1/R2=ok

## Problem

壳自有首启页（`wwwroot/index.html`）是用户对桌面壳的第一眼，却是一套与 dsh Web 层完全不同的视觉语言：紫罗兰主题（`#7c3aed` 主色 + `#8b5cf6` hover）、整页硬编码暗色（`#0f0f13`）不随主题、1.8em 大标题 + 彩色装饰的信息层级。companion 设置页已按 `--dsw-alias-*` 语义 token 对齐官方设置区块规格，boot 页成为壳内唯一的异类。根因：boot 页渲染时 dsh 主题 CSS 尚未加载（运行时未就绪页可能长时间驻留——首启安装要跑分钟级），当年选择硬编码暗色而非做主题回退。

## Decision

按官方 boot-page 同款策略重做该页样式（`deepseek-harness` `packages/client/web/src/boot-page.module.css`，其注释明言「框架外的 boot 页不能依赖主题下发成功」）：

1. **本地回退调色板**：`:root` 定义 `--dshdt-*` 语义变量（label-primary/secondary/tertiary、border、brand、surface、success、error），亮色为基（官方 boot 页同值：label `#0f1115/#61666b/#81858c`），暗色经 `prefers-color-scheme` 整组覆盖（与 companion 同源值：success `#22c55e`、error `#ef4444`）；`color-scheme: light dark` + `background: Canvas` 让底色随系统。刻意不用 `--dsw-alias-*` 作消费口——boot 页加载时这些变量必然不存在，写它只会制造假对齐。
2. **墨色单色系**：brand 从紫罗兰改为官方 boot 页的墨色（亮 `#0f1115`/暗 `#f9fafb`）；语义色只用于 done/failed 的步骤点、failed 行文本与错误框。
3. **排版对齐官方规格**：h1 降为 16px/600 + 0.08em 字距 wordmark（官方 boot 页同值，测试钉住的 `>DeepSeek Harness Desktop</h1>` 契约保留）；spinner 换官方 20px conic-gradient 弧环；步骤行 13px、次级 label 色；按钮主态 = 反色填充（与 companion `.ovn-btn` 同规格）、次态 = 描边幽灵；chip/log/error 全走 token。
4. **文案与 JS 零改动**：所有 data-i18n 键、EN 词典、帧处理逻辑不动——UiCopy 双语对账门禁与既有测试契约不触碰。
5. **companion 链接失败 toast 主题化**：`client.js` 内嵌 CSS 的硬编码暗色（`#22222e/#3a3a4a/#e6e6ea`）改 `--dsw-alias-bg-layer-3/border-l2/label-primary`（toast 生于 dsh 页面内，alias 变量必然已定义；回退值仅防御）。

## Alternatives considered

- **boot 页消费 `--dsw-alias-*` + 本地回退**（同官方 wordmark 的 `var(--dsw-alias-x, var(--dsh-boot-x))` 双层写法）：落败——dsh 主题 CSS 在本页永远不会加载，alias 消费口是死代码；官方那样写是因为它 boot 后仍在同一文档，我们的页会被导航替换。
- **宿主经 `desktop.ui.getLocale` 同路命令下发主题**：落败——主题查询命令、帧排队（`pendingFrames`）都要扩契约面，而 `prefers-color-scheme` 零成本覆盖 boot 期场景；boot 页与 dsh 层主题短暂不一致可接受（分属两文档）。
- **跟随 `body[data-ds-dark-theme]` 属性**：落败——该属性由 dsh 主题运行时设置，同样永远轮不到本页；留作未来 boot 页并入 dsh 文档时再启用。
- **顺手把 RecoveryPageBuilder 恢复页也换皮**：越界——恢复页走 C# 侧拼装，另有 UiCopy 消费面，本批不扩散。

## Consequences

收益：壳首启体验与 dsh 视觉语言同族，主题自适应（暗/亮系统偏好皆有着落）；壳内自有页面 token 体系收敛为一套。代价：boot 页失去紫色品牌记忆点（本来也不在 dsh 品牌色板里）；`prefers-color-scheme` 与 dsh 应用内主题选择可能短暂不一致（两文档各随其源，接受）。

## Testing

`dotnet test` 全绿（898/898，含 `ShellDocuments_HaveVisibleText_StayAlive` 对 h1 契约与 fallbackText 的钉死）；`verify-ui-copy` 绿（文案零改动的对账证明）；`verify-adr-format`/`verify-md-links` 绿；`verify-review-tier --staged` 判 LIGHT；页面渲染人工目检（亮/暗两档）。

## Related

- [online-first-unbundled-runtime](../architecture/2026-08-29-online-first-unbundled-runtime.md)（boot 页职责来源）。
- [review-to-machine-gates](../process/2026-09-13-review-to-machine-gates.md)（UiCopy 门禁来源）。
