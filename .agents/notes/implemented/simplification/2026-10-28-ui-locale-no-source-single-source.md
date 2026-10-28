# Agent Note: UI 缺省语言的单一事实源（含页面空 locale 归一）

Status: implemented

Review: FULL/2026-10-28#5/R1=ok R2=ok R3=ok

## Problem

「没有 locale 来源」在宿主里有三种各自为政的表达：

1. `UiLocale.DefaultLanguage` 返回 `UiLanguage.Zh`（生产 6 处引用：横幅 3、标题栏 1、托盘 1、更新提示 1）。
2. `UiLocale.ResolveInitial` 在持久值非法且探测端口拿不到值时写字面量 `"zh-CN"`。
3. `UiLanguageResolver.Resolve(null/空白)` 返回 `UiLanguage.En`。

再者页面 `wwwroot/index.html` 的 `applyLocale` 用 `locale || 'zh'`——**空白按中文**，与解析处第 3 条**空白按英文**相反。生产路径当前都踩不到（宿主 `desktop.ui.getLocale` 恒回非空、`UiLocale` 自身保证初始值非空），但两处契约相反，任何一侧改动都会让缺省方向分叉。

第三处还有一个真实分叉：`locale || 'zh'` 只挡 falsy，纯空白字符串（`"   "`）会走「未注册语言」分支——页面被整体翻成英文并写 `<html lang="en">`。

## Decision

1. **一处定义、两个面**：`UiLanguageResolver.NoSourceLocale`（规范标记 `"zh-CN"`）与 `NoSource`（产品缺省语言 `Zh`）。两个面**不可互相推导**：由标记反推语言要回 `Resolve`，而 `Resolve` 的空白分支正取 `NoSource`，绕成自引用；`NoSourceLocale` 一旦被改成空白串，那就是无限递归。两者一致由 `UiLocaleTests.NoSource_IsOneFactAcrossAllDefaults` 钉死（标记→语言、语言=中文、三个缺省入口同答）。
2. **两种输入在解析处显式分道**：`Resolve(null/空白)` 取 `NoSource`；**有值但未注册**（`ko-KR` 等）仍回落 `En`，与上游 `FALLBACK_LOCALE = 'en'` 同向。空白不是「未注册语言」，它是没有来源。
3. **派生**：`UiLocale.DefaultLanguage => UiLanguageResolver.NoSource`；`ResolveInitial` 返回 `UiLanguageResolver.NoSourceLocale`，本文件不再写字面量；`OsLocaleProbe` 的两处回退（`CurrentUICulture` 为空、读文化名抛错）同样取 `NoSourceLocale`——生产启动路径真正走到的「无来源」在探测面，`ResolveInitial` 的守卫分支只是端口未接线时的兜底。
4. **页面归一**：`applyLocale` 先 `trim`，`null`/空白落 `NO_SOURCE_LOCALE = 'zh-CN'`；页面不能引用 C# 常量，故两侧各写一次、注释互相锚定（页面侧注释点名 `UiLanguageResolver.NoSourceLocale`，C# 侧注释点名 `wwwroot/index.html`）。
5. **BOM 在探测面剥**：`OsLocaleProbe.ToBcp47` 先删 U+FEFF 再归一，整串只有 BOM 的取值等同未设置（顺延下一个键）。BOM 不是空白、.NET 的 `Trim` 不删它，不剥就会让主语言子标签变成 `"\uFEFFzh"`、整族用户落进「未注册」→ 英文回落。
6. **方向为什么是中文**：产品主要受众是中文用户，页面静态文案与 `<html lang>` 首屏本身就是中文；「没有来源」若回英文，桥断场景下中文用户会看到整页英文。

## Alternatives considered

- **收敛到英文**（`DefaultLanguage → En`、页面空白 → `en`）：落败。它把 6 处「未接线 `UiLocale`」的横幅/标题栏/托盘/更新提示文案改成英文，并把页面首屏从中文整体翻成英文——改的是用户可见面，而问题只是内部三处表达不一致；代价比为统一而统一大。
- **保持现状，只在文档写清「两种情形不同」**：落败。文档消不掉「空白」在页面与解析处得到相反答案这件事；纯空白输入的页面分叉是实打实的（见 Problem 末段）。
- **构建期把 C# 常量生成进页面**：落败。为一处字面量引入代码生成与新的构建面，收益不抵成本；注释锚定 + 两侧各自的判据足够。
- **`Resolve` 只接受非空、空白抛异常**：落败。`Resolve` 的输入面正是「可能没有来源」的探测/回读结果；抛异常把缺省处理摊给每个调用方，单一事实源反而变成多处。

## Consequences

收益：缺省方向只在一处定义；「空白」与「未注册」不再同路；页面与原生壳对同一输入给出同一答案；`ResolveInitial` 的字面量消失。

代价与残留：

- **页面字面量与 C# 常量是两份**（页面无法引用 C#），一致性靠注释与评审维持。本仓无页面 JS 测试面，这条没有机器判据。
- **两套空白字符类不完全等价**：JS `trim()` 删 U+FEFF 不删 U+0085，.NET 的 `Trim`/`IsNullOrWhiteSpace` 相反。BOM 已在探测面剥掉（唯一可达入口——持久化回读与 companion 上报都过 `IsPlausibleLocale`，非字母数字即拒），U+0085 只能由合成输入到达页面。故「两侧同输入同答案」在可达输入上成立，在合成输入上不成立且无判据。
- **`Resolve(null/空白)` 的语义从英文改为中文**：公共契约变更。当前唯一消费方是 `UiLocale.Language`（其值恒非空），故无行为迁移，但外部消费方若自行调用需按新契约读。

## Testing

- `UiLocaleTests.NoSource_IsOneFactAcrossAllDefaults`：`NoSource` / `DefaultLanguage` / `Resolve(null|""|"   ")` / `Resolve(NoSourceLocale)` / 无端口的 `UiLocale().Current` 全部同值——把三个派生面钉在一个断言里。
- `UiLocaleTests.Language_ResolvesEightLocaleClosedSet` 删掉「空值 → En」行：该行实测传的是 `"en"`（`Set("")` 会抛），从未测到空白，留着会把已废的契约伪装成有覆盖。
- `OsLocaleProbeTests.Detect_BomPrefixedEnvValue_StripsBom` / `Detect_BomOnlyEnvValue_FallsThrough`：BOM 前缀归一、只有 BOM 时顺延。
- `OsLocaleProbeTests.Detect_EmptyCultureName_FallsBackToNoSource` / `Detect_InvalidCulture_Throws_NotPropagated`：生产启动路径的末级回退取 `NoSourceLocale`（改动常量两侧一起动，不跟随即红）。
- 页面 `applyLocale` 无自动化面，靠人工与评审（见 Consequences）。
- 全量 `dotnet test` 与门禁全绿（0 警告）；测试计数与覆盖率的机器家是 `scripts/test-baseline.json`，本篇不复制。

## Related

- [ui-copy-eight-locales](../feature/2026-10-28-ui-copy-eight-locales.md)：八语言封闭集与「未注册 → 英文」的定向；本篇只把「没有来源」从那条规则里拆出来。
- [ui-copy-extractor-lib-and-value-invariants](../process/2026-10-28-ui-copy-extractor-lib-and-value-invariants.md)：同批补的「整表与 en 全等」值判据。
