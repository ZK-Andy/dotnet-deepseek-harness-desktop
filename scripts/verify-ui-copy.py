#!/usr/bin/env python3
"""verify-ui-copy — UI 文案单一词典门禁（批次 A，ADR review-to-machine-gates）。

词典 = src/DeepSeek.Harness.Desktop.Core/Localization/UiCopy*.cs（唯一事实源；按域拆 partial 组）。
校验四条不变量：
1. UI 消费文件（C#）不得再出现 CJK 字符串字面量——全部文案必须经 UiCopy；
   两处结构豁免：诊断调用行（`_log.Invoke`/`HostLog.Write` 等，`[tag]` 前缀按行判）；
   `new BootstrapProgress(step, "…")` 的进度行
   （引导进度只进诊断面，页面仅在失败时展示 fail 文案，故不入典）；
2. wwwroot/index.html 的每个 `data-i18n` 节点必须自带非空首屏文案（静态页是首启第一个可见面）；
3. index.html 的八语言表 `var D = { <lang>: { … }, … }` **按页面自身结构**核：
   七种非中文语言各须一张表、键集跨语言全等、`data-i18n`/`data-i18n-aria` 节点与表键双向覆盖、
   译文非空、同语言「最小化/最大化」可达名不得同形、非中文语言表不得与 en 逐键全等（整表未译）；zh 走页内内联节点（`var D` 不得含 zh 副本），
   其标题栏三个可达名对着生产词典 `UiCopy.CaptionButtonNames` 的真实源码逐值核；
4. plugins/dsh-desktop-companion/client/client.js 的八本字典键集相等（zh 为键集事实源，
   键漏一边即漏译）；值面：每键须有非空字面量译文、非 zh/ja 不得含汉字、与 zh 全等须在豁免册内、
   非豁免键不得整本与 en 逐键全等。

host.log 诊断行不属 UI 文案：字面量带 `[host]` 等 log 形态前缀的豁免（已知边界：
以 `[tag]` 开头的真 UI 文案理论上可借道，风险可忽略——UI 文案没有该书写惯例）。
不变量 3 的提取边界：`var D = {` 须独占声明行，其对象体到其后首个行首 `}`/`};` 为止；
体内每种语言是一个 `  <lang>: { … },` 块，取到下一个语言块或对象体收尾为止（两层各一轮，
不做通用 JS 解析）。提取为空即报错——形态一变就 fail loud，不得退化成恒真。比较前统一解码 `\\uXXXX` 等转义（姊妹文件惯用 `\\u2026` 写
省略号，两侧写法可能不同）。拼接串（`正在安装 ` + 插件名）登记的正是前段本身，故全等成立。
不变量 1 的提取边界：注释行（`//`/`///`/`*`/`/*` 起头）不参与提取——注释是散文，不是文案。
自测：--self-test（临时夹具，正反十八例）。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

# 提取层在 scripts/lib/（与 shell 共享库同层）：把该目录挂进 sys.path 再 import。
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

from gate_common import repo_root  # noqa: E402

REPO = repo_root()
SRC = REPO / "src" / "DeepSeek.Harness.Desktop"
CORE = REPO / "src" / "DeepSeek.Harness.Desktop.Core"
INFRA = REPO / "src" / "DeepSeek.Harness.Desktop.Infrastructure"

# UI 消费文件（相对各自工程根）：这些文件里禁止出现 CJK 字符串字面量（文案一律走 UiCopy）。
# 新增 UI 消费文件时把文件加进此表并把文案迁入 UiCopy。
# 已知盲区：CS_LITERAL 不覆盖 @"..." 逐字串与 """...""" 原始串——消费文件引入该形态时须同步扩提取器。
CS_CONSUMERS = [
    (SRC, "Tray/TrayMenuActions.cs"),
    (SRC, "Update/UpdateBanner.cs"),
    (CORE, "Localization/UiLocale.cs"),
    (SRC, "Recovery/RecoveryPageBuilder.cs"),
    (SRC, "Recovery/RecoveryDialogText.cs"),
    (SRC, "PageBridge/DesktopBanner.cs"),
    (SRC, "PageBridge/CaptionBar.cs"),
    (SRC, "PageBridge/PagePump.cs"),
    (SRC, "Tray/TrayCheckFeedback.cs"),
    (SRC, "Hosting/WebView2StartupGuard.cs"),
    (INFRA, "Runtime/RuntimeBootstrap.cs"),
    (INFRA, "Runtime/RuntimeBootstrap.Pure.cs"),
    (INFRA, "Runtime/RuntimeBootstrap.Engine.cs"),
    (INFRA, "Bootstrap/FirstBootBootstrapService.cs"),
]

INDEX_HTML = SRC / "wwwroot" / "index.html"
# 标题栏可达名的生产词典（zh 侧逐值核对）：按方法体定位，不合并整族 UiCopy*.cs
CHROME_COPY = CORE / "Localization" / "UiCopy.Chrome.cs"

# 伴随插件客户端：zh/en 两本字典必须同键（不变量 4 的对账对象，不进 CS_CONSUMERS）。
CLIENT_JS = REPO / "plugins" / "dsh-desktop-companion" / "client" / "client.js"
CLIENT_JS_LABEL = "plugins/dsh-desktop-companion/client/client.js"

from ui_copy_extract import (  # 提取层单源：scripts/lib/ui_copy_extract.py
    CAPTION_KEYS_ORDER,
    CJK,
    LOG_LINE,
    client_decl,
    extract_caption_zh_names,
    extract_client_keys,
    extract_client_values,
    extract_cs_literals_scoped,
    extract_html_locale_tables,
    extract_i18n_keys,
    extract_i18n_node_text,
    extract_static_caption_labels,
)

# 八语言集从 Core 的 `UiLanguage` 枚举源码抽出——语言集的唯一家是枚举，门禁侧不手抄，
# 新增语言时枚举一变这里自动跟随（client.js 的 `var <lang>` 名同源于此）。
_UILANGUAGE_SRC = (CORE / "Localization" / "UiLanguage.cs").read_text(encoding="utf-8")
_ENUM_BODY = _UILANGUAGE_SRC.split("public enum UiLanguage", 1)[1].split("}", 1)[0]
# 枚举成员是「缩进 + 首字母大写标识符 + 逗号」的行；注释行与属性/方法不匹配该形态。
CLIENT_LOCALES = tuple(
    m.group(1).lower() for m in re.finditer(r"^[ \t]+([A-Z][A-Za-z0-9]*)[ \t]*,?[ \t]*$", _ENUM_BODY, re.MULTILINE))
if not CLIENT_LOCALES:
    raise SystemExit("verify-ui-copy: 未能从 UiLanguage 枚举抽到语言集（形态变更？）")

# 值判据的豁免：这些键的译文跨语言**合法地**同形，实测共 5 组——
#   ja↔zh 三组（`updGroup`/`updateTitle` 的「更新」、`diagFailSep` 的「：」，日语本就借用同一书写），
#   en↔de 一组（`updGroup` 的 `Update`），标点一组（`diagFailSep` 的 `': '` 在 en/es/de/ru/pt 相同）。
# 清单只减不增：新增豁免须能举出「该键在两种语言里本就该同值」的证据，否则放行的是漏译。
VALUE_SHARED_OK = frozenset({"updGroup", "updateTitle", "diagFailSep"})


def _check(
    cs_files: list[tuple[str, str]],
    html_text: str,
    client_text: str,
    chrome_text: str,
) -> list[str]:
    """四条不变量的唯一实现（真实仓库与 self-test 夹具共用，防双实现漂移）。

    夹具直接给文本入参；消息里的位置标签用生产常量。
    """
    failures: list[str] = []
    index_label = "wwwroot/index.html"
    copy_label = "UiCopy*.cs"
    client_label = CLIENT_JS_LABEL

    # 不变量 1：消费文件零 CJK 字面量（注释、诊断调用、引导进度帧三处豁免）
    for name, text in cs_files:
        for lit in extract_cs_literals_scoped(text):
            if CJK.search(lit) and not LOG_LINE.match(lit):
                failures.append(f"{name}: CJK 字面量未走 UiCopy 词典：{lit}")

    # 不变量 2：index.html 的每个 data-i18n 文本节点必须自带非空首屏文案。
    # 结构性判据：节点存在 ↔ 有文案（其他语言由不变量 3 的表覆盖）。引导页是静态文档，
    # zh 首屏文案的事实源就是页面本身。
    node_text = extract_i18n_node_text(html_text)
    if not node_text:
        # 抽不到文本节点即报：否则「文本节点被改写成纯 aria 挂载」会让本段与下面的 zh≠en
        # 值判据一起恒真（两条判据共用这份提取结果，静默退化的代价是双份）。
        failures.append(f"{index_label}: 未能提取到 data-i18n 节点文本（声明形态变更？）")
    for key, text_value in node_text.items():
        if not text_value.strip():
            failures.append(f"{index_label}: data-i18n 节点 {key} 缺首屏文案")

    # 不变量 3：index.html 八语言表按**页面自身结构**核（语言闭集 / 键集跨语言全等 /
    # 节点⇄表键双向覆盖 / 非空 / 孪生可达名）。
    tables = extract_html_locale_tables(html_text)
    if not tables:
        failures.append(f"{index_label}: 未能从 var D 提取到语言表（声明形态变更？）")
    # 语言维度闭集：七种非中文语言各须有一张表（zh 走页内内联节点，不在 D 里）。
    # 只查「多」不查「少」会让整块语言表被删后静默通过——那是一种界面语言直接消失。
    missing_langs = sorted(set(CLIENT_LOCALES) - set(tables) - {"zh"})
    if missing_langs:
        failures.append(f"{index_label}: var D 缺少语言表：{missing_langs!r}（七种非中文语言各须一张表）")
    # `CLIENT_LOCALES` 含 zh，故直接做差会让「D.zh 复辟」静默通过；zh 必须不在 var D 里
    # （它走页内内联节点，副本会成为无人读的第二份事实）。
    unexpected = sorted((set(tables) - set(CLIENT_LOCALES)) | (set(tables) & {"zh"}))
    if unexpected:
        failures.append(f"{index_label}: var D 含未登记语言：{unexpected!r}（语言集的唯一家是 UiLanguage 枚举）")

    # 判据按**页面自身的结构**核：核对对象是「页面是否自洽」，不是「抄件是否一致」。
    keys_by_lang = {lang: set(entries) for lang, entries in tables.items()}
    non_zh = [lang for lang in CLIENT_LOCALES if lang != "zh"]
    # ① 键集跨语言全等：七种非中文语言的键集必须完全一致（漏一条即漏译）
    reference = keys_by_lang.get(non_zh[0], set())
    for lang in non_zh[1:]:
        if lang not in keys_by_lang:
            continue
        if keys_by_lang[lang] != reference:
            missing = sorted(reference - keys_by_lang[lang])
            extra = sorted(keys_by_lang[lang] - reference)
            failures.append(
                f"{index_label}: {lang} 表键集与 {non_zh[0]} 不一致：缺少 {missing!r}，多出 {extra!r}")
    # ② 页内可翻译节点 ⇄ 表键 双向覆盖：data-i18n / data-i18n-aria 的每个键都必须有译文，
    #    表里的每个键也必须在页面上真实存在（写了没人用的键 = 文案会被静默遗忘）。
    node_keys = extract_i18n_keys(html_text)
    if not node_keys:
        # 提取不到节点即报：否则「页内改写成单引号属性」这类形态变更会让②整段恒真（静默通过）。
        failures.append(f"{index_label}: 未能提取到 data-i18n / data-i18n-aria 节点键（声明形态变更？）")
    else:
        for lang in non_zh:
            if lang not in keys_by_lang:
                continue
            if missing := sorted(node_keys - keys_by_lang[lang]):
                failures.append(f"{index_label}: {lang} 表缺少页内节点键：{missing!r}")
        for lang in non_zh:
            if lang not in keys_by_lang:
                continue
            if unused := sorted(keys_by_lang[lang] - node_keys):
                failures.append(f"{index_label}: {lang} 表含页面上不存在的键：{unused!r}")
    # ③ 同语言内「最小化/最大化可达名不得相同」：两键同值是写反或复制的典型形态。
    #    这条无白名单——es/pt 的 Minimizar/Maximizar 是两国语同形词，但同语言内两键仍不同值，
    #    故不存在需要豁免的合法情形（早期版本按 (语言, 值) 白名单会吞掉「es 的 capMaximize
    #    被误抄成 Minimizar」这一真阳性，已删）。
    for lang in non_zh:
        if lang not in keys_by_lang:
            continue
        pair = (tables[lang].get("capMinimize"), tables[lang].get("capMaximize"))
        if pair[0] is not None and pair[0] == pair[1]:
            failures.append(f"{index_label}: {lang} 的最小化与最大化可达名相同（{pair[0]!r}）——可达名写反或漏译")
    # ③ 值非空：空译文会让节点被渲染成空白（比缺键更隐蔽）
    for lang, entries in tables.items():
        for key, value in entries.items():
            if not value.strip():
                failures.append(f"{index_label}: {lang} 表 {key} 的译文为空")
    # ④ zh 侧可达名对着真实生产词典核（页内静态条的 aria-label 是文案，不是副本）：
    #    zh 走内联节点、没有 D.zh 表，故只能核这一处有生产事实源可对的键。
    zh_caption = extract_static_caption_labels(html_text)
    if not zh_caption:
        # 同理：可达名整条消失（或属性形态变更）不得静默通过。
        failures.append(f"{index_label}: 未能提取到静态标题栏的 aria-label + data-i18n-aria（声明形态变更？）")
    # 三个可达名必须齐全：删掉其中一个（哪怕其余仍在）也不得静默通过。
    missing_caption = [k for k in CAPTION_KEYS_ORDER if k not in zh_caption]
    if missing_caption:
        failures.append(f"{index_label}: 静态标题栏缺少可达名键：{missing_caption!r}")
    expected_zh = extract_caption_zh_names(chrome_text)
    if not expected_zh:
        failures.append(f"{copy_label}: 未能从 CaptionButtonNames 抽到 zh 可达名（形态变更？）")
    for key, label in zh_caption.items():
        expected = expected_zh.get(key)
        if expected is None:
            failures.append(f"{index_label}: 页内可达名 {key} 在 CaptionButtonNames 里无对应项")
            continue
        if label != expected:
            failures.append(f"{index_label}: 页内可达名 {key} 与生产词典不符：{label!r} != {expected!r}")

    # 不变量 3 的值面：页内 zh 内联文案必须**真的不同于**同键英文译文——
    # 结构判据（键齐/非空/可达名）盖不住「整块中文被英文顶替」：那八条内联文案曾只有结构判据，
    # 中文写成 'Retry' 也全绿。这里只核 zh≠en（同形品牌词在页面上不存在，故不留豁免）。
    en_table = tables.get("en") or {}
    for key, zh_text in node_text.items():
        en_text = en_table.get(key)
        if en_text is not None and zh_text.strip() == en_text.strip():
            failures.append(f"{index_label}: 页内 zh 文案 {key} 与 en 译文全等（{zh_text!r}）——未译或抄错语言")

    # ⑤ 整表与 en 逐键全等 = 整表未译：拉丁语种躲得过不变量 4 的汉字判据，这是漏译的第二形态。
    #    豁免册里的键跨语言本就同形，不计入证据——否则「只有品牌词与 en 相同」的合法表会被误判。
    for lang in [lg for lg in non_zh if lg != "en"]:
        judged = {k: v for k, v in (tables.get(lang) or {}).items() if k not in VALUE_SHARED_OK}
        if judged and all(en_table.get(k) == v for k, v in judged.items()):
            failures.append(f"{index_label}: {lang} 表与 en 逐键全等（{len(judged)} 键）——整表未译")

    # 不变量 4：client.js 八本字典——键集全等（zh 为事实源，漏一边即漏译）+ 值面三判据
    #   ② 每键必须有字面量译文且非空（键在、值缺失曾是静默盲区：键集相等掩盖整本英文残留）；
    #   ③ 非汉字语种（ja 除外，它本就混用汉字）不得出现汉字——整本译文被 zh 顶替的典型形态；
    #   ④ 值跨语言全等须在豁免册内（品牌词/标点本就同形），否则是「漏译后互相抄」。
    per_locale: dict[str, set[str]] = {}
    values_by_locale: dict[str, dict[str, str]] = {}
    for name in CLIENT_LOCALES:
        keys = set(extract_client_keys(client_text, client_decl(name)))
        if not keys:
            failures.append(f"{client_label}: 未能从 var {name} 提取到键（声明形态变更或该语言缺字典？）")
        per_locale[name] = keys
        values_by_locale[name] = extract_client_values(client_text, client_decl(name))
        for key, value in values_by_locale[name].items():
            if not value.strip():
                failures.append(f"{client_label}: {name} 的 {key} 译文为空")
        if name not in ("zh", "ja"):
            leaks = sorted(k for k, v in values_by_locale[name].items() if CJK.search(v))
            if leaks:
                failures.append(f"{client_label}: {name} 的值含汉字（未译或整本沿用 zh）：{leaks!r}")
    zh_keys = per_locale.get("zh") or set()
    zh_values = values_by_locale.get("zh") or {}
    en_values = values_by_locale.get("en") or {}
    # zh 是键集与值的事实源：它自己的值退化成引用时，下面的「与 zh 全等」会拿 None 逐键跳过。
    if lacking := sorted(k for k in zh_keys if k not in zh_values):
        failures.append(f"{client_label}: zh 的 {lacking!r} 无字面量译文（值形态变更？）")
    for name in non_zh:
        keys = per_locale.get(name) or set()
        if not zh_keys or not keys:
            continue
        if missing := sorted(zh_keys - keys):
            failures.append(f"{client_label}: {name} 缺少键：{missing!r}")
        if missing := sorted(keys - zh_keys):
            failures.append(f"{client_label}: {name} 多出键（zh 未登记）：{missing!r}")
        # 值面：字面量值缺失即报——键在而值为引用/拼接形态，门禁看不到内容，等于无判据
        if lacking := sorted(k for k in keys & zh_keys if k not in values_by_locale[name]):
            failures.append(f"{client_label}: {name} 的 {lacking!r} 无字面量译文（值形态变更？）")
        # 跨语言同值：逐键分组，只放过豁免册里的键
        for key in sorted(keys & zh_keys):
            value = values_by_locale[name].get(key)
            if value is None or value != zh_values.get(key) or key in VALUE_SHARED_OK:
                continue
            failures.append(f"{client_label}: {name} 的 {key} 译文与 zh 全等（{value!r}），漏译或未进 VALUE_SHARED_OK")
        # ⑤ 整本未译：非豁免键全都与 en 同键同值（拉丁语种躲得过汉字判据）
        if name == "en":
            continue
        judged = {k: v for k, v in (values_by_locale.get(name) or {}).items() if k not in VALUE_SHARED_OK}
        if judged and all(en_values.get(k) == v for k, v in judged.items()):
            failures.append(f"{client_label}: {name} 的字典与 en 逐键全等（{len(judged)} 键）——整本未译")
    return failures


def verify() -> list[str]:
    cs_files = [((base.name + "/" + rel), (base / rel).read_text(encoding="utf-8")) for base, rel in CS_CONSUMERS]
    return _check(
        cs_files,
        INDEX_HTML.read_text(encoding="utf-8"),
        CLIENT_JS.read_text(encoding="utf-8"),
        CHROME_COPY.read_text(encoding="utf-8"),
    )


def self_test() -> int:
    """夹具：不变量 1（消费文件零 CJK）+ 不变量 2（节点有文案）+ 不变量 3（页面结构自洽）
    + 不变量 4（client.js 八本同键）。全部走 _check 的同一实现，防双实现漂移。"""
    # 夹具直接给文本：_check 收的就是字符串，落临时文件再读回是多余往返。
    # 七张最小语言表 + 一条 data-i18n 节点（键 retry），构成自洽的最小页面。
    langs = ("en", "ja", "es", "fr", "de", "ru", "pt")
    table_body = "".join(
        f"  {lg}: {{\n    retry: 'R-{lg}',\n"
        f"    capMinimize: 'm-{lg}',\n    capMaximize: 'M-{lg}',\n    capClose: 'c-{lg}',\n  }},\n"
        for lg in langs)
    caption_bar = (
        '<div id="dsh-desktop-caption-bar">'
        '<button data-webview-close aria-label="关闭" data-i18n-aria="capClose">x</button>'
        '<button data-webview-minimize aria-label="最小化" data-i18n-aria="capMinimize">x</button>'
        '<button data-webview-maximize aria-label="最大化" data-i18n-aria="capMaximize">x</button>'
        "</div>")
    html_ok = (
        "<html><body>" + caption_bar
        + '<button id="b" data-i18n="retry">重试</button>'
        + "<script>\nvar D = {\n" + table_body + "};\n</script>"
        + "</body></html>")
    client_ok = "".join(
        f"var {name} = {{\n  alpha: '{'甲' if name == 'zh' else 'A' + str(n)}',\n}}\n"
        for n, name in enumerate(CLIENT_LOCALES))
    chrome_ok = (
        "public static (string Minimize, string Maximize, string Close) CaptionButtonNames(UiLanguage lang) => (\n"
        '    Pick(lang, zh: "最小化", en: "Minimize"),\n'
        '    Pick(lang, zh: "最大化", en: "Maximize"),\n'
        '    Pick(lang, zh: "关闭", en: "Close"));\n')

    # 反例 1：消费文件私藏 CJK 字面量
    f1 = _check([("Tray.cs", 'var label = english ? "Show" : "显示主窗";')], html_ok, client_ok, chrome_ok)
    assert any("UiCopy 词典" in x for x in f1), f1

    # 正例：log 形态豁免 + 页面自洽
    f2 = _check([("Tray.cs", 'HostLog.Write("[host] 写入失败：{ex.Message}");')], html_ok, client_ok, chrome_ok)
    assert f2 == [], f2

    # 反例 3：某语言整表被删（一种界面语言静默消失）
    pt_block = ("  pt: {\n    retry: 'R-pt',\n"
                "    capMinimize: 'm-pt',\n    capMaximize: 'M-pt',\n    capClose: 'c-pt',\n  },\n")
    html_missing_lang = html_ok.replace(pt_block, "")
    f3 = _check([], html_missing_lang, client_ok, chrome_ok)
    assert any("缺少语言表" in x and "pt" in x for x in f3), f3

    # 反例 4：非拉丁文案多出一个页面上不存在的键（旧实现被 ASCII_LETTER 整族放过）
    html_cyrillic = html_ok.replace("retry: 'R-ru',", "retry: 'R-ru',\n    ghost: 'новый текст',")
    f4 = _check([], html_cyrillic, client_ok, chrome_ok)
    assert any("ghost" in x for x in f4), f4

    # 反例 5：同语言两键译文相同（可达名写反/复制）
    twin = (
        "<html><body>"
        '<button id="b" data-i18n="retry">重试</button>'
        "<script>\nvar D = {\n" + "".join(
            f"  {lg}: {{\n    retry: 'R-{lg}',\n    capMinimize: '同',\n    capMaximize: '同',\n  }},\n"
            for lg in langs) + "};\n</script></body></html>")
    f5 = _check([], twin, client_ok, chrome_ok)
    assert any("可达名相同" in x for x in f5), f5

    # 反例 6：空译文（比缺键更隐蔽）
    html_blank = html_ok.replace("retry: 'R-ja',", "retry: '   ',")
    f6 = _check([], html_blank, client_ok, chrome_ok)
    assert any("译文为空" in x for x in f6), f6

    # 反例 7：页内节点缺键（表里有、页面没有 → 文案被静默遗忘）
    orphan = html_ok.replace('data-i18n="retry"', 'data-i18n="retryX"')
    f7 = _check([], orphan, client_ok, chrome_ok)
    assert any("缺少页内节点键" in x for x in f7), f7

    # 反例 8：data-i18n 节点缺首屏文案
    html_no_text = html_ok.replace('data-i18n="retry">重试<', 'data-i18n="retry"><')
    f8 = _check([], html_no_text, client_ok, chrome_ok)
    assert any("缺首屏文案" in x for x in f8), f8

    # 反例 9：zh 可达名与生产词典不符（属性值不进文本节点提取，旧实现完全无判据）
    caption_block = (
        '<div id="dsh-desktop-caption-bar">'
        '<button data-webview-close aria-label="X" data-i18n-aria="capClose">x</button></div>')
    table_with_cap = "".join(
        "  " + lg + ": {\n    retry: 'R-" + lg + "',\n    capClose: 'C-" + lg + "',\n  },\n"
        for lg in langs)
    with_caption = (
        "<html><body>" + caption_block
        + '<button id="b" data-i18n="retry">重试</button>'
        + "<script>\nvar D = {\n" + table_with_cap + "};\n</script></body></html>")
    f9 = _check([], with_caption, client_ok, chrome_ok)
    assert any("可达名 capClose 与生产词典不符" in x for x in f9), f9

    # 反例 10b：生产词典抽不到 zh 可达名（签名不匹配 / 少一个 Pick）——必须 fail loud，
    # 不得因「凑不够三个」而静默跳过整段判据。
    chrome_broken = (
        "public static (string Minimize, string Maximize, string Close) CaptionButtonNames(UiLanguage x) => (\n"
        '    Pick(x, zh: "最小化", en: "Minimize"),\n'
        '    Pick(x, en: "Maximize"),\n'
        '    Pick(x, zh: "关闭", en: "Close"));\n')
    f10b = _check([], html_ok, client_ok, chrome_broken)
    assert any("未能从 CaptionButtonNames 抽到 zh 可达名" in x for x in f10b), f10b

    # 反例 10：client.js 八本字典键不一致
    client_bad = (
        "var zh = {\n  alpha: '甲',\n}\n"
        "var en = {\n  beta: 'B',\n}\n"
    )
    f10 = _check([], html_ok, client_bad, chrome_ok)
    assert any("en 缺少键" in x or "en 多出键" in x for x in f10), f10
    assert sum("未能从 var" in x for x in f10) == 6, f10

    # 反例 12：页面 zh 内联文案被英文顶替（结构全对、值错——旧判据全绿）
    html_en_swapped = html_ok.replace('data-i18n="retry">重试<', 'data-i18n="retry">Retry<')
    # 夹具 en 表的 retry 是 'R-en'，故再补一条同值场景：让 en 表 retry 与节点同值
    html_en_swapped = html_en_swapped.replace("retry: 'R-en',", "retry: 'Retry',")
    f12 = _check([], html_en_swapped, client_ok, chrome_ok)
    assert any("页内 zh 文案 retry 与 en 译文全等" in x for x in f12), f12

    # 反例 13：client.js 非汉字语种的值整本沿用 zh（键集相等，旧判据全绿）
    client_zh_leak = client_ok.replace("  alpha: 'A1',", "  alpha: '甲',", 1)
    f13 = _check([], html_ok, client_zh_leak, chrome_ok)
    assert any("en 的值含汉字" in x for x in f13), f13
    assert any("en 的 alpha 译文与 zh 全等" in x for x in f13), f13

    # 反例 14：client.js 某键值缺失（值形态变引用，门禁看不到内容）
    client_no_value = client_ok.replace("alpha: 'A1',", "alpha: someConst,")
    f14 = _check([], html_ok, client_no_value, chrome_ok)
    assert any("en 的 ['alpha'] 无字面量译文" in x for x in f14), f14

    # 反例 15：豁免册内的键跨语言同值不得报（否则品牌词会被判成漏译）
    client_shared = "".join(
        f"var {name} = {{\n  updateTitle: '{'更新' if name == 'zh' else 'Update'}',\n}}\n"
        for name in CLIENT_LOCALES)
    f15 = _check([], html_ok, client_shared, chrome_ok)
    assert not any("updateTitle" in x for x in f15), f15

    # 反例 11：var D 整体消失（声明形态变更不得静默通过）
    f11 = _check([], "<html><body></body></html>", client_ok, chrome_ok)
    assert any("未能从 var D 提取到语言表" in x for x in f11), f11

    # 反例 16：client.js 某语言整本与 en 同值（拉丁语种躲得过汉字判据，旧判据全绿）
    client_de_en = client_ok.replace("  alpha: 'A5',", "  alpha: 'A1',")
    f16 = _check([], html_ok, client_de_en, chrome_ok)
    assert any("de 的字典与 en 逐键全等" in x for x in f16), f16

    # 反例 17：页内某语言整表与 en 同值（同上，只是页面侧）
    de_en_block = ("  de: {\n    retry: 'R-en',\n"
                   "    capMinimize: 'm-en',\n    capMaximize: 'M-en',\n    capClose: 'c-en',\n  },\n")
    html_de_en = html_ok.replace(
        "  de: {\n    retry: 'R-de',\n"
        "    capMinimize: 'm-de',\n    capMaximize: 'M-de',\n    capClose: 'c-de',\n  },\n",
        de_en_block)
    f17 = _check([], html_de_en, client_ok, chrome_ok)
    assert any("de 表与 en 逐键全等" in x for x in f17), f17

    # 提取层自己的夹具也挂在这条门禁路径上：库改动若无人执行，形同没有夹具
    # （CI 与两个 hook 只跑本脚本的 --self-test）。
    import ui_copy_extract

    if ui_copy_extract._self_test() != 0:
        print("FAIL 提取层夹具（scripts/lib/ui_copy_extract.py）")
        return 1

    print("self-test OK (18 cases + 提取层 6 组)")
    return 0


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()
    failures = verify()
    for f in failures:
        print(f"FAIL: {f}")
    print("OK" if not failures else f"{len(failures)} 处违例")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
