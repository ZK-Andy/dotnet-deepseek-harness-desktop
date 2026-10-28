# Agent Note: 宿主 UI 文案八语言化（对齐上游 ja/es/fr/de/ru/pt）

Status: implemented

Review: FULL/2026-10-28#4/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 3300，fanout ≤ 20，packages ≤ 1（一次性：为 45 条文案补齐六种语言，必然要写满六本字典——`file` 帽已在批内用分部与并排排版压到下限内（UiCopy 拆四个分部、client.js 每语言并排），剩下的只是总量；按语言切分会留下「字典在但没人选」的不可用中间态。读数随批内修复走动：补回 en 侧登记常量、`var D` 八语言表校对、`CaptionBar` 回落键与两处自测迁移后实测 2664；门禁结构判据化后实测 2623（帽值按实测上浮约 3%）。批次入库后豁免自动失效；批 C 折入后实测 3159——`scripts/lib/ui_copy_extract.py` 迁出 335 行提取层（同时把门禁主体压回帽内）、client.js 值与页内 zh 值判据、`UiCopy.ListSeparator` 收编内联分号，帽值按实测上浮约 4%）。

## Problem

上游 0.2.2-alpha.2 把界面语言扩到八种（`packages/client/locale/src/locales/` 下 zh/en/ja/es/fr/de/ru/pt，每本全量字典；官方 Electron 桌面端的 `apps/desktop/src/locale.ts` 同样是八本原生壳字典；安装器另有 8 份 `xx_XX.json`）。我方宿主壳（托盘菜单、横幅、恢复页/对话框、自绘标题栏、引导页、伴生插件设置区）此前只有中英两分支。

缺陷不在「少配了字典」，而在**判定方向**：`UiLocale.IsEnglish => _locale.StartsWith("en")`，即「非 en 一律按中文」。上游的语言包用户在宿主壳上会看到中文——托盘菜单、崩溃恢复对话框、WebView2 缺失提示、标题栏读屏可达名全是中文。上游对同一问题给出的方向写在 `packages/client/locale/src/client/index.ts` 的 `FALLBACK_LOCALE = 'en'` 注释里：未指名已注册语言的使用者最不可能读中文，所以残差指向英文。

我方 `host-ui-locale` ADR 正文当时写的是「对齐 dsh 字典查找链 ns → common → en 的兜底方向」，但代码与此相反——文字与实现分叉，且没有任何测试覆盖「ja 用户看到什么」。

## Decision

八语言化按一条主线落地：**语言集封闭、字典全量、兜底指向英文**。

1. **语言键**：新增 `UiLanguage`（Zh/En/Ja/Es/Fr/De/Ru/Pt）与 `UiLanguageResolver`（按主语言子标签解析，分隔符认 `-`/`_`/`.`——OS 探测面给的是 POSIX 形态 `zh_CN.UTF-8`；未注册语言一律 `En`；空白不是未注册，取缺省语言单点 `NoSource`，见 [ui-locale-no-source-single-source](../simplification/2026-10-28-ui-locale-no-source-single-source.md)）。这是 locale→语言判定的唯一家，消费方不再各自写正则。

   **缺省语言单点**：`UiLocale.DefaultLanguage`（中文，委托 `UiLanguageResolver.NoSource`）供各消费方「可选 `UiLocale` 参数」共用。它与「未注册 locale 回落英文」不矛盾——前者是**没有 locale 来源**的缺省展示，后者是**已知 locale 但语言未注册**，两条在解析处显式分道。
2. **字典形态**：`UiCopy` 的 `bool english` 全部换成 `UiLanguage lang`，每条文案经私有 `Pick(lang, zh:…, en:…, ja:…, es:…, fr:…, de:…, ru:…, pt:…)`。**具名实参**是刻意的：漏一种语言编译即失败，不需要测试兜底。带参文案统一 `string.Format(InvariantCulture, …, arg)`，占位符从 C# 内插迁到 `{0}`/`{1}`。
3. **兜底方向**：`Language` 是消费方取语言的唯一入口（`IsEnglish` 判据在本批内先降级为 `Language == En`、随后随生产消费者清零删除）。即 ja/es/fr/de/ru/pt 走各自字典，未注册语言（含 ko 等）走英文——与上游 `FALLBACK_LOCALE` 同向。
4. **三处静态面同步**：
   - `wwwroot/index.html`：`var EN` 换成 `var D`（**七种非中文**语言表；zh 走页内内联节点，不设 `D.zh` 副本——那个副本无读者且是第二份事实），`applyLocale` 按主语言子标签取表，分隔符与原生壳一致地认 `-`/`_`/`.`，未注册语言回落 `D.en`；`<html lang>` 写 zh-CN 或该语言标签。
   - `CaptionBar` 注入脚本：`L={zh,en}` 换成八语言对象（键由 `UiLanguage` 枚举单趟产出），页面按 `document.documentElement.lang` 的主子标签索引。回落两段：文档有语言但未注册 → `L.en`；文档根本没给语言（`k` 为空）→ 宿主构建期语言（`UiLocale.DefaultLanguage`）。标题栏按钮可达名因此随 dsh 语言而非宿主构建期语言。
   - `client.js`：`zh/en` 两本扩到八本并一起注册（`ctx.locale.register(NS, {zh,en,ja,es,fr,de,ru,pt})`）。
5. **门禁**：`verify-ui-copy.py` 的不变量 4 从「zh/en 两本同键」升级为「八本同键」（zh 为键集事实源，漏一本即报缺键）。不变量 3 改为核对**页面自身的结构**，不再与 C# 侧手抄常量对账：
   - 语言闭集：七种非中文语言各须有一张 `var D` 表；
   - 键集跨语言全等：七张表的键集必须一致（漏一条即漏译）；
   - 页内节点 ⇄ 表键双向覆盖：每个 `data-i18n`/`data-i18n-aria` 键都要有译文，表里也不能有页面上不存在的键；
   - 译文非空；
   - 同语言「最小化/最大化」可达名不得同形（写反或复制的典型形态；无白名单——同语言内两键同值无合法情形）。

   同时删除了 `UiCopy.cs` 内 16 条 `Index*<Lang>` 常量——那批常量**零生产消费者**，唯一读者是门禁，换来的只是「两份手抄确实一致」而不是「漏译编译报错」。删后判据从「抄件比对」变为「页面自洽」，`zh` 侧标题栏可达名改由真实生产词典 `UiCopy.CaptionButtonNames` 核对（此前属性值不进文本提取，完全无判据）。

## Alternatives considered

- **只把兜底改成英文、不做翻译**（最小改动：`IsEnglish` 改成 `Language != Zh`）：落败。修好了「不给中文」，但 ja/es/fr/de/ru/pt 用户拿到的仍是英文，与上游「八语言完整字典」不对等；用户明确要求字典匹配。
- **保留 `bool english`，另加 `UiLanguage` 并行入口**：落败。两套判定共存必然漂移（本仓已有「文字与实现分叉」的先例，即本次 Problem 段本身）；一次性改签名让编译器兜住全部调用点。
- **用 resx + ResourceManager 文化链**：落败。语言集是封闭枚举（八种），resx 的卫星程序集装配与 `CultureInfo` 回退链对这个场景是纯开销，且会把「漏一种语言」从编译期错误降级为运行时静默回落——正是本批要消灭的失效模式。
- **字典按语言拆成 8 个 partial 文件**：落败。译文与源串必须并排才可核对；拆开后任何一条改文案都要跨 8 个文件定位，且门禁无法机械验证「同一键八种语言都改了」。
- **只翻译壳的可见面、跳过 `index.html`/`client.js`**：落败。引导页是首启第一个可见面（语言未定时显示），跳过它等于把最容易暴露语言错配的界面留在两分支。
- **`OkLabel` 也断言八语言互异**：落败（测试阶段实证）。日语「OK」与英文同形是正确译法，机械断言「八串互异」会对正确译文报错；改为断言真正应当互异的条目。
- **把六本新字典外置成独立模块文件**：落败。页面侧 `factory(require)` 的 `require` 只解析模块表内的包名 specifier，**不支持相对路径**（上游全部插件无一处相对 `require`）；外置会让 `client.js` 加载期抛错、整条 web boot 中断。改为同文件并排排版压体积。

## Consequences

收益：六种新增语言的用户在宿主壳上看到母语，而非中文或英文；`UiLanguageResolver` 成为 locale 判定的唯一家；漏译从此是编译错误或门禁失败，不是线上现象。

代价与残留：

- **词典体积**：`UiCopy` 由单文件拆成四个分部（`UiCopy.cs` 选择器 + `Bootstrap`/`Chrome`/`FailureDialog`，最大分部 `Bootstrap` 正好 300 行），`client.js` 净增 42 行（六本字典按语言五组并排书写）。拆分部与并排排版都是为守 `file-max`（+150/文件）与 F1；代价是「一条文案的八种语言」不再集中在一屏内，需跨分部或跨组定位。
- **译文质量未经验证**：八语言译文由本会话生成，**未经母语者复核**（【未验证】）。机械门禁只能保证键集齐全、无空串，保证不了语气与术语一致性。这是本批最大的未证项，用户可见后在真实语言环境下才可能暴露。
- **第三方插件不受影响**：`client.js` 的 NS 是我们自己的命名空间；其他插件仍按自己的语言覆盖回落。
- **`index.html` 的静态文案只有 zh 有生产词典对照**：删掉 `Index*<Lang>` 常量后，七种非中文语言的译文只存在于页内 `var D`，由不变量 3 的结构判据（键集/覆盖/非空/孪生名）保证自洽。代价是「某条 ja 译文被改成另一条 ja 文案」这类**值语义**错误无机械判据（键集不变、值非空），只能靠译文复核。zh 侧标题栏可达名有真实词典可对，已纳入判据。
- **POSIX 形态在 companion 入口被静默丢弃**：`CompanionLocaleCommandRouter` 用 `UiLocale.IsPlausibleLocale` 拦掉带 `_` 的形态（该判据只认 BCP 47 的 `-`），拦掉即返回 `"null"`、**不调用 `Set`、不记日志**。故若上游某版本把 `html[lang]` 写成 `zh_CN`，宿主语言会**停在旧值**而不是「判对但格式不标准」。`UiLanguageResolver` 认 `_`/`.` 是纵深防御（防直接 `Set` 或未来宿主绕过路由器），生产路径当前不走它——`OsLocaleProbe` 已在探测面把 POSIX 归一成 BCP 47。

## Testing

- `UiLocaleTests.Language_ResolvesEightLocaleClosedSet`（11 例 Theory）：八语言 + `en-GB`/`zh-Hans` 两种变体 + `ko-KR`（唯一未注册）逐条钉解析结果；空白输入的缺省方向由 `NoSource_IsOneFactAcrossAllDefaults` 钉（见 [ui-locale-no-source-single-source](../simplification/2026-10-28-ui-locale-no-source-single-source.md)）。
- `UiLocaleTests.Language_ResolvesPosixLocaleForms`（8 例 Theory）：POSIX 形态（`zh_CN.UTF-8`/`zh_TW.Big5`/`ja_JP.UTF-8`/`pt_BR.UTF-8`/`en_US.UTF-8`/`ko_KR.UTF-8` 等）逐条钉解析——纵深防御的回归网。
- `OsLocaleProbeTests.Detect_NormalizesPosixToBcp47`（8 例 Theory）：归一落点在探测面（剥 `.` 修饰符、`_`→`-`；含 `C`/`POSIX` 两个「未设置」取值）。
- `UiLocaleTests.Language_NonChineseLocales_NeverFallBackToChinese`：六种新增语言**不得**取到中文文案（直接断言托盘「退出」与中文分支不同）——本批的核心回归。
- `UiLocaleTests.UiCopy_AllEightLanguages_DistinctNonEmpty`：八语言逐条非空；抽查三条两两互异。
- `CaptionBarTests.Build_AriaLabels_EightLocaleTablesWithDocumentPick`（3 例 Theory）：注入脚本含八语言表，且回落**两段**形态正确——`L[k]||(k?L.en:L["<宿主语言>"])`。旧形态 `L[k]||L.en||L[fb]` 的第三段恒不可达（`L.en` 恒存在），本轮修复为可达形态。
- 既有 `[Theory]` 的 `[InlineData(false/true)]` 改为 `[InlineData(UiLanguage.Zh/En)]`；`if (english)` 一类判据改为 `lang != UiLanguage.Zh`。
- `verify-ui-copy --self-test`（18 例）：逐例对应一条判据，其中「语言整表被删」「非拉丁文案未登记」「同语言两键可达名相同」「可达名缺失」「节点缺首屏文案」「生产词典抽不到 zh 可达名」六例直指门禁盲区——这些形态此前均静默通过。前五例与门禁改动同批做过变异实证；第六例（抽取失败分支）是同批评审发现漏覆盖后补的。
- 全量 `dotnet test` 全绿（计数与覆盖率基线的机器家在 `scripts/test-baseline.json`，本篇不复制）；`verify-ui-copy`、`verify-code-health`、`verify-code-conventions`、`verify-increment-discipline`（带本批豁免）、`verify-adr-format`、`verify-md-links` 全绿。

## Deferred

- 译文母语复核（八语言 × 45 条）。
- `docs/` 与 README 的语言能力陈述仍按「中英双语」书写，未随本批更新；它们描述的是文档层（`.md` / `.en.md` 镜像），与 UI 语言是两件事，但读者可能混淆，需在文档批次里点明。

## Related

- ADR host-ui-locale（`implemented/feature/2026-08-28-host-ui-locale.md`）：建立宿主 UI 语言单点与 companion 桥接；其正文「非 en 一律按中文」的兜底方向被本篇取代，其余（桥接链路、持久化、托盘重建）继续有效。
- ADR `implemented/process/2026-10-28-ui-copy-extractor-lib-and-value-invariants.md`：把本篇门禁的提取层拆进 `scripts/lib/`，并补 client.js 与页内 zh 的值判据（含 `UiCopy.ListSeparator` 收编内联分号）。
- ADR ui-copy-bilingual-completion（`implemented/feature/2026-09-16-ui-copy-bilingual-completion.md`）：中英双语收口；本篇把「双语」扩为八语言，不改变「词典是唯一事实源」的结论。
- 上游 `packages/client/locale/src/client/index.ts` 的 `FALLBACK_LOCALE = 'en'` 注释：兜底方向的上游依据。
