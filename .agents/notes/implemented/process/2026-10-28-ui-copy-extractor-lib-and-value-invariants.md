# Agent Note: UI 文案门禁的提取层单源与值判据（批 C）

Status: implemented

Review: FULL/2026-10-28#4/R1=ok R2=ok R3=ok

## Problem

`verify-ui-copy.py` 在八语言化批内四轮修复后长到 550 行，距单文件增量帽（`+150` 文件行）只剩两行——门禁本身成了后续任何一次文案判据改动的硬阻塞。同时四条判据只覆盖**结构**，值语义留了三处盲区：

1. `client.js` 的八本字典此前只核**键集**。键齐、值被整本替换成 zh 原文（或某条键的值改成引用/拼接）都全绿，是「漏译」最典型的线上形态。
2. `index.html` 的页内 zh 八条内联文案只有结构判据（节点存在、非空、可达名对生产词典），中文写成 `Retry` 同样全绿。
3. `RuntimeBootstrap.Pure.cs` 里 `lang == UiLanguage.Zh ? "；" : "; "` 把一处文案写死在消费方，绕过词典——`verify-ui-copy` 的 CJK 扫描按注释行/诊断行豁免，这个内联串在 `string.Join` 参数位，不在豁免面上却也没被「词典唯一事实源」的意图覆盖到。

## Decision

1. **提取层迁出**：`scripts/lib/ui_copy_extract.py` 成为 `verify-ui-copy` 的提取实现单源（`scripts/lib/`，与 `scripts/gate_common.py` 同为库层），含正则、`_unescape`/`_strip_*`/`_object_bodies` 与八个 `extract_*`。门禁脚本只留判据、报错文案与自测。拆分的理由是**行数额度**，不是分层美学：门禁要能继续加判据，就不能让提取细节占着 `file-max` 的余额。库自带 6 组夹具（形态变更必须抽空，不得猜），`verify-ui-copy --self-test` 的 18 例仍走同一 `_check`。
2. **client.js 值判据四条**（不变量 4 扩容）：
   - 每个键必须有**字面量**译文且非空——值改成引用/拼接时门禁看不见内容，等于无判据，故报错而非放过；
   - 非 zh、非 ja 的字典不得出现汉字（整本沿用 zh 的典型形态；ja 本就混用汉字，故豁免）；
   - 与 zh 逐键全等的译文必须登记在 `VALUE_SHARED_OK` 豁免册内（品牌词 `Update`：`updGroup`/`updateTitle`；标点 `': '`/`'：'`：`diagFailSep`）；
   - **整本与 en 逐键全等**即报（整本未译）：比较面剔掉 `VALUE_SHARED_OK` 里的键（那些跨语言本就同形），至少一条非豁免键作证。
3. **index.html 值判据两条**：页内 zh 内联文案不得与同键 en 译文全等（`Retry` 顶替「重试」）；非中文语言表同样不得与 en 表逐键全等。
4. **内联分号入典**：`UiCopy.ListSeparator(lang)` 收编该处文案（中文全角分号、其余语言半角分号加空格），消费方只调词典。

**为什么不判「跨语言同值」**：按 `(键, 语种对)` 逐格比对 45 键 × 28 对，实测 25 组同值——14 组落在 `VALUE_SHARED_OK` 三键上（ja↔zh 的「更新」×2 与「：」是豁免的实际理由），11 组在其余键（es/pt 的 `Reiniciando…`、fr/de 的 `Installation…`、en/fr 的 `Diagnostics` 等真同形词）。同语言内重复同样合法（`安装并重启`、`安装中…` 各出现两次）。故「各语言值互不相同」会大面积误报，不能作为判据；值面已落地上述四条，其余留给译文复核。

**为什么「整本与 en 全等」可以判**：它是「整本未译」的判据，方向与上一条相反——只要求**整表**同值才算违例，单键同值是常态。实测零误报：de 与 en 仅 3/45 键同值，es/fr/ru/pt 各 1/45，ja/zh 为 0/45；页内 `var D` 各语言与 en 均 0/11 同值。

## Alternatives considered

- **把 `verify-ui-copy.py` 整体拆成两个门禁**（结构面 / 值面各一个 `verify-*.py`）：落败。`verify-review-tier.py` 对 `verify-*.py` 一律 FULL 档，两个脚本共享同一份解析却要各自自测与各自评审，成本翻倍而判据不变。
- **提取层并入 `gate_common.py`**：落败。`gate_common` 的家是「跨门禁」的通用面（repo_root/slug/C# 行扫描器/SelfTest）；UI 文案提取只有这一个消费者，放进去等于把单消费者逻辑塞进共享库。
- **用增量帽豁免提额**（改 `verify-increment-discipline.py` 的帽值或再加一条豁免）：落败。批 B 已用过一次一次性豁免，再开一条会把「门禁改不动」固化成常态。
- **判各语言值互异（每语言每键唯一且跨语言不同）**：落败（实测）。25 组 `(键, 语种对)` 同值里 11 组是非豁免键上的正确译法，机械断言会对正确译文报错。
- **让 `client.js` 值缺失静默跳过**（只核存在的值）：落败。值形态一变门禁就恒真，正是本批要消灭的失效模式。

## Consequences

收益：门禁从 550 降到 441 行（本批补值判据后为 471 行）、解除 +150 的硬阻塞，且后续判据可继续加；`client.js` 与页内 zh 的值语义首次有机械判据；词典唯一的例外（内联分号）收口。

代价与残留：

- **值判据仍不完整**：词典 45 键 × 8 语言的**跨语言抄错**（把 ja 的一条抄成另一条 ja 文案）仍无判据，只有豁免册三键与汉字泄漏两条兜底。
- **「整本某语言其实是英文」只剩单条判据**：整表与 en 逐键全等的形态已判；但「整本英文里改了三两条」仍全绿——那已经不是「整本未译」，交给译文母语复核。
- **提取层新增一次间接**：`verify-ui-copy.py` 多一个 `sys.path.insert` 与跨目录 import；库文件本身不受 shell 规范 S3/S5 管辖（那是 `scripts/lib/*.sh`），故无 linter 覆盖，只有自带夹具。
- **`VALUE_SHARED_OK` 只减不增**的说法依赖评审：豁免册是白名单，加一条即放宽一条判据，机器无法判断「该键在两种语言里本就该同值」。

## Testing

- `python3 scripts/lib/ui_copy_extract.py`：6 组夹具（C# 豁免面 / 页面节点 / CaptionButtonNames 截断 / 静态可达名 / `var D` 表 / client.js 键值双提取）；同一入口也由 `verify-ui-copy --self-test` 调用（执行点：CI 的 `ci.yml` 与本地手动跑门禁；两个 git hook 不跑该脚本）。
- `verify-ui-copy --self-test`：18 例，其中六例直指值面盲区——页内 zh 被 en 顶替、页内某语言整表与 en 同值、非汉字语种整本沿用 zh、client.js 某语言整本与 en 同值、值形态变引用、豁免册内同值不得报。
- `UiLocaleTests.ListSeparator_EachLanguageExactValue`：八语言逐值钉住（zh/ja 全角、fr 法式前置空格、其余半角加空格）。
- 全量 `dotnet test` 与机械化门禁全绿（计数基线在 `scripts/test-baseline.json`，本篇不复制）。

## Related

- ADR `implemented/feature/2026-10-28-ui-copy-eight-locales.md`：建立八语言字典与本门禁的四条不变量；本篇在其上扩值判据、拆提取层，不改变「词典是唯一事实源」。
- `scripts/gate_common.py`：库层的既有先例（ADR `process/2026-09-29-gate-common-shared-module`）；本提取层与其同属库层但不同目录（`scripts/` vs `scripts/lib/`）。
