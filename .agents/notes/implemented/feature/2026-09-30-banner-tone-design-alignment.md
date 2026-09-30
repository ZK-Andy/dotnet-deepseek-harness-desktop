# Agent Note: 横幅族对齐 dsh 主题——BannerTone 语义化与 alias token 表面

Status: implemented

Review: LIGHT/2026-09-30#1/R2=ok

## Problem

恢复页批 R2 的简报外发现（挂账收口）：`DesktopBanner` 非受控退出横幅仍硬编码紫罗兰配色。排查后确认这是**一族**问题——三张宿主横幅（版本底线/非受控退出/更新就绪）共用 `Build` 工厂、各自硬编码四色 `Palette`（暗红底、暗夜底紫按钮、暗绿底），全是 boot 页对齐前的旧皮肤。横幅与 boot/恢复页语境不同：它注入在 dsh 正常页面之上，dsh 主题 CSS 必然在场，该消费 `--dsw-alias-*` 而非自研回退组。

## Decision

1. **`Palette` record 退役，换 `BannerTone` 枚举 `{Warn, Neutral, Success}`**：调用方只声明语义（底线 = Warn、非受控退出 = Neutral、更新就绪 = Success），token 映射收拢到 `Build` 单点——横幅皮肤的语义决定只剩一个家。
2. **表面中性化**：底 = `var(--dsw-alias-bg-overlay, #fff)`（横幅本质是覆盖层，主题有专门的 overlay 面 token）、文字 = `label-primary`、分隔线 = `border-l2`，亮暗主题自适应；不再整片深色底压页面。
3. **语义色只点缀按钮**：Warn → `state-warn-primary`（琥珀）、Success → `state-success-primary`（绿）、Neutral → label-primary 反色填充（文字 = bg-overlay，亮暗互为镜像）；hover 语义不变（原实现无 hover 样式，维持）。
4. **回退链**：每个 token 带硬编码 fallback，纯防御（与 companion toast 同款论证）；token 名已对随包主题 bundle 实测枚举（`state-warn-primary` 等全部存在）。
5. **零行为面**：DOM 结构、id、堆叠偏移、JsString 注入通道、文案全不动。测试同步：`Build_AppliesPalette` → `Build_AppliesToneTokens`（断言 token 映射而非合成 hex），其余四例仅换参。

## Alternatives considered

- **保留 `Palette`，调用方直接传 `var(--dsw-alias-…)` 字符串**：落败——最小 diff 但语义决定仍散在三个调用点，每个调用方自行挑 token，回到「镜像漂移」老路（本工厂的立项理由就是消除它）。
- **用 `--dshdt-*` 回退调色板**（boot/恢复页同款）：落败——语境不同。boot/恢复页渲染时 dsh 主题必然不在场；横幅注入时主题必然在场。两套场景两套 token 家族，混用会造成假对齐。
- **语义色整片底（state-*-tertiary 色底）**：落败——bundle 枚举未见表级 tertiary 背景 token（仅 success-tertiary 在 companion 用例中出现过），引入未经验证的 token 违背「名字实测」纪律；且整片色底违背 dsh 克制用色风格。
- **只修被点名的非受控退出横幅**：落败——三张同厂同构，修一张留两张，下一批还得再来一次。

## Consequences

收益：横幅族随 dsh 亮暗主题自适应，紫罗兰残留清零；横幅皮肤语义决定单点化（`Palette` 退役后公共面 -1）。代价：注入脚本体积略增（token 串长于原 hex）；Neutral 反色按钮在极端品牌主题下对比度依赖主题自身的 label-primary/bg-overlay 配对（信任主题 token 契约）。

## Testing

`dotnet test` 全绿 898/898 零警告（DesktopBannerTests 五例：新 `Build_AppliesToneTokens` 断言三 tone 的按钮 token 与中性表面；其余 id 守卫/堆叠/转义/locale 例原样）；`verify-adr-format`/`verify-ui-copy`/`verify-md-links` 绿；`verify-review-tier --staged` 判 LIGHT。

## Related

- [boot-page-dsh-design-alignment](2026-09-30-boot-page-dsh-design-alignment.md)（壳自有页面 `--dshdt` 回退组来源；其 Alternatives 与本篇划清两套 token 家族的语境边界）。
- [recovery-page-design-alignment](2026-09-30-recovery-page-design-alignment.md)（恢复页批，其 R2 简报外发现即本篇起点）。
- [host-ui-locale](2026-08-28-host-ui-locale.md)（横幅 locale 语义来源）。
