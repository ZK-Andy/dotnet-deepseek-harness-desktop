# Agent Note: UI 文案双语补齐——宿主自绘面、引导页与门禁

Status: implemented
Review: FULL/2026-09-16/R1=ok R2=ok R3=ok

## Problem

宿主侧 UI 双语（ADR [host-ui-locale](2026-08-28-host-ui-locale.md) 与 companion 客户端 i18n）落地后，用户把 dsh 切成英文仍会在四处看到中文：

1. **托盘「检查更新」结果通知正文**（`TrayCheckFeedback`）与 **引导失败 reason**（`RuntimeBootstrap` 的失败文案，经引导页错误框呈现）硬编码中文——`UiCopy` 的文档注释曾把这两处登记为「用户可见但未收编的存量」。
2. **首启引导页**（`wwwroot/index.html`）整页中文单语：它是 dsh 起来之前的宿主静态页，既没有英文文案，也没有任何语言来源。
3. 引导页文案**没有英文侧门禁**：`verify-ui-copy` 只认 CJK，英文文案漏登记/漏渲染都不红；companion 客户端 `client.js` 的 `zh`/`en` 双字典本批前完全在门禁之外（漏一个 en 键只会静默退化显示 key）。
4. 宿主自绘的**恢复页**整页写死中文（`RecoveryPageBuilder` 的骨架/接线模板 8 处 `english: false`，`BuildScript` 也没有语言形参），英文语言下只有崩溃原因一处是英文。

第 2 点的根因是**语言来源缺失**：引导页渲染在 dsh 启动之前，那时 companion 还没跑、`html[lang]` 桥按定义还没建立；只靠 OS locale 会重蹈 host-ui-locale 已消掉的症状（dsh 语言设置独立于系统语言）。

## Decision

**四缺口同一批补齐，语言判定沿用既有单点（`UiLocale`），新增的只有「语言来源」与「门禁面」。**

### 1. 两处存量文案入典

`TrayCheckFeedback.Message(state, english)`、`RuntimeBootstrap`（含 `.Pure`/`.Engine` partial）的全部失败文案、插件的引导结论与步骤文案（`FirstBootBootstrapService`）改经 `UiCopy` 双参入口（`TrayCheck*` / `Bootstrap*` / `Preinstall*`）；`RecoveryPageBuilder.BuildScript` 增语言形参，恢复页骨架、按钮与导出状态随语言（原先连崩溃原因都只在调用点硬传 `false`）。判据：**凡能进引导页错误框、页面日志区或系统通知的文案，一律双语**。`host.log` 诊断行与引导进度帧（`report(new BootstrapProgress(step, "…"))`，页面从不渲染其文案——失败时展示的是 Fail 文案）保持中文单语，由门禁按语句结构豁免而非入典。

`english` 由 `FirstBootBootstrapService` 经 `Func<bool>` 惰性注入（与 `Func<HarnessRuntimeHost>` 同一延迟捕获理由：`UiLocale` 在组合根的单实例仲裁处构造，引导任务实际启动时必已就绪），再逐层传入 `RuntimeBootstrap.RunAsync` / hooks / 私有步骤方法。

### 2. 引导页双语：宿主拉取 + 页面字典

- 页面侧：中文文案**留在 HTML 原位**（默认渲染，门禁照旧核对），可切换元素加 `data-i18n`；英文收在页面内唯一新增对象字面量 `EN`（21 键）。`applyLocale(locale)` 按 `/^en/i` 决定是否换文案并设 `document.documentElement.lang`；动态串（插件名/安装中/推荐徽标/跳过结论）经 `T(key, zh)` 取词，中文兜底内联。
- 语言来源：页面启动 `window.__ryn.invoke('desktop.ui.getLocale')` **主动拉取**，宿主新增强类型命令路由 `UiLocaleCommandRouter`（帧 `{"locale":"en"}`，经 `AppJsonContext` 源生成注册）。命令不可用/被拒 → 页面保持中文（增强能力语义）。
- 为什么是拉而不是推：引导页加载与宿主注入之间没有就绪信号，宿主早期注入的脚本可能先于页面脚本执行而丢失；页面主动 invoke 无时序竞态。页面是 dsh 起来前的瞬态页，运行中不再切换语言。

### 3. 引导页语言来源：持久化上次 dsh 语言

`UiLocale` 增 `IUiLocaleStore` 端口（Core）+ `DesktopUiLocaleStore` 实现（Infrastructure，落 `<dsh-home>/profiles/dotnet-desktop/ui-locale`，与端口/PID 记忆同族）。初始值 = 持久值（`UiLocale.IsPlausibleLocale` 形态合法才采信）→ OS locale 兜底；companion 每次上报即更新并落盘。形态判据同时收编 `CompanionLocaleCommandRouter` 原先的私有副本（单一事实源）。

### 4. 门禁扩容为四条不变量

`scripts/verify-ui-copy.py`：①消费文件 CJK 字面量必须入典（既有；消费清单本批扩到引导与托盘面：`TrayCheckFeedback`、`DesktopTrayCommandRouter`、`RuntimeBootstrap{,.Pure,.Engine}`、`FirstBootBootstrapService`，并新增「注释行不参与提取」「诊断调用与引导进度帧按语句抹白」两处结构豁免——插值串会被字面量提取器切成碎片，行级判定挡不住跨行三元）；②`index.html` CJK 文案必须登记（既有）；③`index.html` 的 `EN` 字典 ↔ `UiCopy` 的 `<Name>En` 常量**双向全等核对**（页面漏登记与词典悬空登记都拦；包含判定会放过短串落在长登记串里的漏登记）；④companion `client.js` 的 `zh`/`en` 键集必须相等且非空。自测 4 例扩到 10 例。

## Alternatives considered

- **让 `RuntimeBootstrap` 抛语义码、由 Presentation 翻译（值流化）**：更彻底的分层（Infrastructure 不碰 UI 文案）。落败原因：需同批改 `IFirstBootUi`/`BootstrapOutcome` 契约与页面帧形状，扩散面远大于「同层加一个 `bool`」；且失败文案要带前缀/退出码/摘要等参数，语义码必须携带同样参数才能重建文案，收益被成本吃掉。留作后续值流批次的可选项。
- **引导页语言由宿主推（注入脚本/CustomEvent，复用 `dsh-desktop-bootstrap` 帧）**：省一条 IPC 命令。落败原因：页面脚本就绪无信号，宿主注入成功 ≠ 页面已注册监听；引导不需要时根本没有帧，fallback 文案会停在中文。拉取是唯一无竞态的形态。
- **引导页语言只取 OS locale（零新增持久化）**：改动最小。落败原因：dsh 语言独立于 OS locale，中文系统上把 dsh 切成英文的用户每次启动都会看到中文引导页——正是 host-ui-locale 当初要消掉的症状。
- **宿主读 dsh `settings.yaml` 的 `locale.preference`**：不用新增状态文件。落败原因：引入第二状态源与读时机竞态，host-ui-locale 已就同类方案记录落败；持久化宿主自己收到的上报值没有这个问题。
- **resx / ResourceManager 资源**：正统 .NET 本地化。落败原因：沿用 ADR [review-to-machine-gates](../process/2026-09-13-review-to-machine-gates.md) 的结论——只有中/英两分支，卫星程序集与文化链是纯开销，强类型入口让编译器保证引用完整。
- **引导页出两份静态页（`index.html` + `index.en.html`）**：实现最直白。落败原因：整页重复，两份都要跟着状态机/样式演进，门禁也得成对维护；`data-i18n` + 单 `EN` 字典的增量更小。
- **只补门禁（④）不做双语页**：落败原因：门禁只防未来回归，不解决用户当下看到的英文缺口。
- **把引导进度帧的文案也逐条登记入典**：可让门禁少一处结构豁免。落败原因：进度帧页面从不渲染（失败时展示的是 Fail 文案），把它们放进「用户可见文案的唯一家」等于让词典承担诊断面语料，且为不可见串维护 12 份双语条目；结构豁免（按语句抹白）表达的是同一事实且更薄。
- **诊断豁免按行判（行内含 `_log.Invoke(` 即豁免）**：实现更短。落败原因：插值串被提取器切段（`$"…{string.Join(", ", xs)}…"`）、跨行三元把调用起点留在上一行，行级判定漏掉续行碎片（实测漏报 2 处）；语句级抹白到分号才是与书写形态无关的判据。

## Consequences

- dsh 语言与 OS 语言不一致时：**首次启动**（还没有持久值）引导页仍按 OS locale；此后按上次 dsh 语言。
- 新增一个小状态文件 `<dsh-home>/profiles/dotnet-desktop/ui-locale`；读写失败都只告警并回退 OS locale，不阻断启动。
- 引导页英文只在页面加载时应用一次（引导页是 dsh 起来前的瞬态页，语言再切换时页面已消失）。
- 新增 IPC 命令 `desktop.ui.getLocale`（强类型帧，`AppJsonContext` 注册）；companion 侧无改动。
- 顺带修正：恢复页崩溃原因随语言（原写死 `english: false`）；引导失败兜底「未知错误」入典。
- 门禁强度提升：英文侧漏登记/悬空登记（全等判定）、client.js 键漏配、以及引导/托盘面的新增中文文案从此可机器拦截；两处结构豁免（诊断调用、引导进度帧）是有意留出的单语面。
- 插件显示名靠约定（`pluginName<Id>` 键 + 未知 id 回落原 id）：新增 preset 插件若不同步加 `EN` 键，英文界面显示插件 id 而非译名——回落可见、不误导，登记项由门禁的 EN 双向核对兜住页面侧。

### Testing

- `UiLocaleTests`：持久值优先、非法持久值回退、上报即落盘且同值不重写、形态判据边界表。
- `RecoveryPageBuilderTests`：`english: true` 下按钮/重试说明/导出状态全英文（不留中文半面）。
- `DesktopUiLocaleStoreTests`：profile 目录往返、缺失/空白/被目录占用一律 null（`dsh-home-env` 集合串行）。
- `UiLocaleCommandRouterTests`：帧形状、随最新值、只认自己命令名。
- `RuntimeBootstrapTests`：npm 失败/权限两路的 `english: true` 分支（并断言不混入中文）。
- `TrayTests`：三个结束态的英文文案与英文兜底原因。
- `verify-ui-copy.py --self-test`：10 例（新增 EN 未登记、`En` 常量悬空、client.js 键不齐、注释/诊断/进度三豁免与真 UI 文案同文件共存、EN 短串落在长登记串里须拦、`En` 常量与 EN 值仅包含须拦）。

### Related

- [host-ui-locale](2026-08-28-host-ui-locale.md)：宿主侧语言单点与 `html[lang]` 桥（本决定补其页面侧与持久化）。
- [companion-client-i18n](2026-08-28-companion-client-i18n.md)：companion 客户端接入 dsh `ctx.locale`（桌面设置页双语）。
- [review-to-machine-gates](../process/2026-09-13-review-to-machine-gates.md)：`UiCopy` 词典与 `verify-ui-copy` 的由来。
