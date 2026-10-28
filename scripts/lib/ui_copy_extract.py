#!/usr/bin/env python3
"""UI-copy 提取器族——index.html 语言表、client.js 字典与 C# 文案面字面量的**唯一**提取实现。

家法：本模块是 `scripts/verify-ui-copy.py` 的提取层单源（门禁只留判据与报错文案），
与 `scripts/gate_common.py` 同层，供 `python3 scripts/verify-ui-copy.py` 直接 `import`。

面（四个不变量的原料，不含判定）：
  - C# 侧：`extract_cs_literals_scoped` 抽文案面字面量（注释行、诊断调用、引导进度帧按语句豁免）；
    `extract_caption_zh_names` 从生产词典 `UiCopy.CaptionButtonNames` 源码抽 zh 侧可达名。
  - 页面侧（index.html）：`extract_i18n_node_text`/`extract_i18n_keys` 抽 `data-i18n` 节点文案与键，
    `extract_static_caption_labels` 抽静态标题栏可达名，`extract_html_locale_tables` 抽 `var D` 八语言表。
  - 插件侧（client.js）：`extract_client_keys`/`extract_client_values` 抽 `var <lang> = {` 字典。

提取边界（形态一变即抽空，调用方必须 fail loud，不得退化成恒真）：
  - `var D = {` / `var <lang> = {` 须独占声明行，对象体到其后首个行首 `}`/`};` 为止；
  - `var D` 体内每种语言是一个 `  <lang>: { … },` 块，取到下一个语言块或对象体收尾（两层各一轮，
    不做通用 JS 解析）；
  - C# 注释行（`//`/`///`/`*`/`/*` 起头）不参与提取——注释是散文，不是文案。

本模块是库不是入口（无可执行位、`__main__` 仅跑自测）：`python3 scripts/lib/ui_copy_extract.py`
跑夹具，0 = 通过，1 = 失败。
"""
from __future__ import annotations

import re
import sys

CJK = re.compile(r"[一-龥]")
# C# 字符串字面量（逐段粗提取足够：词典与消费文件均为普通 "..." 形态，无逐字字符串）
CS_LITERAL = re.compile(r'"(?:[^"\\\n]|\\.)*"')
# host.log 诊断行形态（[host]/[bootstrap]/… 前缀）：即使在 UI 消费文件里也是诊断而非 UI 文案，豁免
LOG_LINE = re.compile(r'^"\[[a-z-]+\]')
# 引导进度帧（report(new BootstrapProgress(step, "…"))）：只进诊断面与页面进度帧，页面从不渲染其
# 文案（失败时展示的是 Fail 文案），故与诊断行同族豁免——同样按语句抹白到分号。
PROGRESS_MARKER = re.compile(r"new\s+BootstrapProgress\s*\(")
# 诊断调用（`_log.Invoke`/`log?.Invoke`/`HostLog.Write`）：插值串会被字面量提取器切成多段
# （`$"…{string.Join(", ", xs)}…"` 与跨行三元），故按**语句**抹白到分号，比行级判定稳。
DIAG_MARKER = re.compile(r"(?:\b\w*[Ll]og|\bHostLog)\s*\??\.\s*(?:Invoke|Write)\s*\(")
# C# 注释行起头形态：注释是散文，其引号不参与字面量提取
COMMENT_LINE = ("//", "*", "/*")

# —— 不变量 3：index.html 的 `var D = { <lang>: { … } }` 八语言表 ——
# 声明独占一行；收尾允许 `};` 与 `}`（姊妹文件 client.js 用的是无分号的 `}`）。
D_DECL = re.compile(r"^[ \t]*var[ \t]+D[ \t]*=[ \t]*\{[ \t]*$", re.MULTILINE)
OBJECT_END = re.compile(r"^[ \t]*\};?[ \t]*$", re.MULTILINE)
# 语言块起头：`  ja: {`（键可为裸标识符或引号；缩进后紧跟子对象）
LANG_BLOCK = re.compile(r"^[ \t]+['\"]?([A-Za-z][A-Za-z0-9_]*)['\"]?[ \t]*:[ \t]*\{[ \t]*$", re.MULTILINE)
# JS 引号串（转义原文保留，比较前统一解码）；注释抹白，注释里的引号不参与提取
JS_NOISE = re.compile(r"'(?:\\.|[^'\\\n])*'|\"(?:\\.|[^\"\\\n])*\"|//[^\n]*|/\*[\s\S]*?\*/")

# CaptionButtonNames 的签名（参数名可改，故用 \w+ 而非钉住 `lang`）
CAPTION_NAMES_SIG = re.compile(r"CaptionButtonNames\s*\(\s*UiLanguage\s+\w+\s*\)")
# `Pick(lang, zh: "…")` 参数组里的 zh 值（CaptionButtonNames 的三组各有其一）
CS_COMMENT = re.compile(r"/\*[\s\S]*?\*/|//[^\n]*")
PICK_ZH = re.compile(r"zh:\s*\"((?:[^\"\\\n]|\\.)*)\"")
# `data-i18n="k"` 节点的内联文本（到闭合标签为止；节点内允许嵌 <code>）
I18N_NODE = re.compile(r"data-i18n=\"([A-Za-z_$][A-Za-z0-9_$]*)\"[^>]*>([^<]*(?:<code>[^<]*</code>[^<]*)*)<")
# 页内可翻译节点的键（文本节点与可达名两种挂载方式）
I18N_ATTR = re.compile(r"data-i18n(?:-aria)?=\"([A-Za-z_$][A-Za-z0-9_$]*)\"")
# 静态标题栏按钮：aria-label 值 + data-i18n-aria 键（顺序固定为 aria-label 在前）
CAPTION_BUTTON = re.compile(r"<button[^>]*\baria-label=\"([^\"]*)\"[^>]*\bdata-i18n-aria=\"([^\"]+)\"")
# CaptionButtonNames 的三个 Pick(lang, zh: …) 参数顺序固定为 (最小化, 最大化, 关闭)；
# zh 侧期望值**从生产词典源码里抽**，不在门禁侧手抄（手抄件改动词典时不会报）。
CAPTION_KEYS_ORDER = ("capMinimize", "capMaximize", "capClose")

# 字典/页内表的一项：`键: '值'`（单双引号皆收，值内转义原文保留）；一个形状只留这条正则。
JS_KEY_VALUE = re.compile(r"(?:^|[,{\n])[ \t]*([A-Za-z_$][A-Za-z0-9_$]*)[ \t]*:[ \t]*(['\"])((?:\\.|(?!\2)[^\\\n])*)\2")
# client.js 字典键：裸标识符（值/注释先抹白，值里的 `, key:` 才不会误判成键）
JS_BARE_KEY = re.compile(r"(?:^|[,{\n])[ \t]*([A-Za-z_$][A-Za-z0-9_$]*)[ \t]*:")
JS_QUOTED_KEY = re.compile(r"(?:^|[,{\n])[ \t]*(['\"])((?:\\.|(?!\1)[^\\\n])+)\1[ \t]*:")

_ESCAPES = {"n": "\n", "r": "\r", "t": "\t", "\\": "\\", "'": "'", '"': '"', "0": "\0"}


def client_decl(name: str) -> re.Pattern[str]:
    """client.js 里某语言的字典声明形态：`var <name> = {` 独占一行。"""
    return re.compile(r"^[ \t]*var[ \t]+" + name + r"[ \t]*=[ \t]*\{[ \t]*$", re.MULTILINE)


def _unescape(s: str) -> str:
    """解码 JS/C# 通用转义（`\\uXXXX`/`\\xXX`/`\\n` 等）：同一文案两侧写法可能不同。"""
    def one(m: re.Match[str]) -> str:
        body = m.group(1)
        if body[:1] == "u" and len(body) == 5:
            return chr(int(body[1:], 16))
        if body[:1] == "x" and len(body) == 3:
            return chr(int(body[1:], 16))
        return _ESCAPES.get(body, body)

    return re.sub(r"\\(u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|.)", one, s)


def _blank(m: re.Match[str]) -> str:
    """把匹配片段抹成空白（保留换行）：位置与原文不变，只留结构不留值。"""
    return re.sub(r"[^\n]", " ", m.group(0))


def _strip_cs_comments(text: str) -> str:
    """抹掉 C# 的 `//` 与 `/* */` 注释（保留长度以便定位）：注释里的 `zh:` 不是词典值。"""
    return CS_COMMENT.sub(lambda m: " " * len(m.group(0)), text)


def _strip_js_comments(body: str) -> str:
    """抹白注释、保留字符串（等长，位置对齐）——引号键与 EN 字面量都还要在原文位置上找。"""
    def one(m: re.Match[str]) -> str:
        s = m.group(0)
        return _blank(m) if s.startswith(("//", "/*")) else s

    return JS_NOISE.sub(one, body)


def _object_bodies(text: str, decl: re.Pattern[str]) -> list[str]:
    """取 `decl`（形如 `var X = {`）到其后首个行首 `}`/`};` 之间的对象体。"""
    bodies = []
    for m in decl.finditer(text):
        end = OBJECT_END.search(text, m.end())
        if end:
            bodies.append(text[m.end():end.start()])
    return bodies


def _blank_statement_spans(text: str, marker: re.Pattern[str]) -> str:
    """把每个 marker 命中处的**语句**（到其后首个分号）换成空白，保留行结构。

    语句级而非行级：诊断/进度调用常写成跨行三元或含嵌套引号（`{x ?? ""}`），
    字面量提取器会把它们切成多段碎片；按行判会漏掉续行上的碎片。
    """
    out: list[str] = []
    in_span = False
    for line in text.splitlines():
        if in_span:
            end = line.find(";")
            if end < 0:
                out.append("")
                continue
            in_span = False
            line = line[end + 1:]
        match = marker.search(line)
        if match:
            end = line.find(";", match.end())
            if end < 0:
                in_span = True
                line = line[:match.start()]
            else:
                line = line[:match.start()] + line[end + 1:]
        out.append(line)
    return "\n".join(out)


def extract_cs_literals_scoped(text: str) -> list[str]:
    """文案面字面量：注释行整行跳过，诊断调用与引导进度帧按语句抹白后再提取。"""
    body = "\n".join("" if line.strip().startswith(COMMENT_LINE) else line for line in text.splitlines())
    body = _blank_statement_spans(body, DIAG_MARKER)
    body = _blank_statement_spans(body, PROGRESS_MARKER)
    return CS_LITERAL.findall(body)


def extract_i18n_node_text(text: str) -> dict[str, str]:
    """抽 `data-i18n="k"` 节点的内联文本：键 → 节点里的首屏文案（去标签）。"""
    out: dict[str, str] = {}
    for m in I18N_NODE.finditer(text):
        out[m.group(1)] = re.sub(r"<[^>]*>", "", m.group(2))
    return out


def extract_i18n_keys(text: str) -> set[str]:
    """抽页内可翻译节点的键：`data-i18n="k"` 与 `data-i18n-aria="k"`。"""
    return set(I18N_ATTR.findall(text))


def extract_caption_zh_names(chrome_text: str) -> dict[str, str]:
    """从 `UiCopy.CaptionButtonNames` 的真实源码抽 zh 侧三个可达名（最小化 / 最大化 / 关闭）。

    取该方法的三个 `Pick(lang,` 参数组，各组的 `zh: "…"` 值；顺序即 CAPTION_KEYS_ORDER。
    返回空 dict 表示没抽到（调用方须报错，不得静默通过）。
    """
    sig = CAPTION_NAMES_SIG.search(chrome_text)
    if sig is None:
        return {}
    # 只取本方法的方法体（到其后首个顶层 `;` 为止）：该文件后面紧跟的 CaptionFullscreenName
    # 也含 `zh:`，不截断会把「全屏」误当第三个可达名。
    end = chrome_text.find(";", sig.end())
    body = chrome_text[sig.end():end if end >= 0 else len(chrome_text)]
    body = _strip_cs_comments(body)
    # 按 `Pick(` 逐个参数组切：每组一个 zh 值。不按「全文前三个 zh:」扫——注释里的 zh: 会混进来，
    # 且少一个也仍能凑够三个（把「关闭」判成别的值），是最坏的假判据形态。
    zh_values = [m.group(1) for m in PICK_ZH.finditer(body)]
    if len(zh_values) != 3:
        return {}
    return dict(zip(CAPTION_KEYS_ORDER, zh_values, strict=True))


def extract_static_caption_labels(text: str) -> dict[str, str]:
    """抽静态标题栏三个按钮的 zh 可达名：`<button … aria-label="关闭" data-i18n-aria="capClose">`。

    zh 走内联节点、没有 `var D` 表，故属性级文案的 zh 侧只能对着生产词典（`UiCopy.CaptionButtonNames`）
    核这一处——属性值不进入文本节点提取，只有这条判据能覆盖它。
    """
    out: dict[str, str] = {}
    for m in CAPTION_BUTTON.finditer(text):
        out[m.group(2)] = m.group(1)
    return out


def extract_html_locale_tables(text: str) -> dict[str, dict[str, str]]:
    """抽 `var D = { <lang>: { key: '值', … }, … }`：语言键 → {键: 值}（注释不计，转义已解码）。

    键级而非值级：值级配对漏「同语言两键互换」与「两语言同形词互相顶替」两类缺陷。
    """
    tables: dict[str, dict[str, str]] = {}
    for body in _object_bodies(text, D_DECL):
        blocks = list(LANG_BLOCK.finditer(body))
        for i, m in enumerate(blocks):
            end = blocks[i + 1].start() if i + 1 < len(blocks) else len(body)
            chunk = _strip_js_comments(body[m.end():end])
            entries: dict[str, str] = {}
            for km in JS_KEY_VALUE.finditer(chunk):
                entries[km.group(1)] = _unescape(km.group(3))
            tables[m.group(1)] = entries
    return tables


def extract_client_keys(text: str, decl: re.Pattern[str]) -> list[str]:
    """抽 `var zh`/`var en` 字典的键集（裸标识符与引号键；行尾注释不影响）。"""
    keys = []
    for body in _object_bodies(text, decl):
        # 裸键：值（引号串）与注释一律抹白，否则值内的 `, key:` 会被误判成键
        keys += [m.group(1) for m in JS_BARE_KEY.finditer(JS_NOISE.sub(_blank, body))]
        keys += [_unescape(m.group(2)) for m in JS_QUOTED_KEY.finditer(_strip_js_comments(body))]
    return keys


def extract_client_values(text: str, decl: re.Pattern[str]) -> dict[str, str]:
    """抽 `var <lang>` 字典的键→值（字面量值；转义已解码，注释已抹白）。

    值提取的边界：只认 `键: '值'` / `键: "值"` 的字面量形态——非字面量值（引用常量、拼接）
    被跳过，由调用方按「值缺失」报错，不得把缺值当空值放过。
    """
    values: dict[str, str] = {}
    for body in _object_bodies(text, decl):
        for m in JS_KEY_VALUE.finditer(_strip_js_comments(body)):
            values[m.group(1)] = _unescape(m.group(3))
    return values


def _self_test() -> int:
    """夹具：每条提取边界各一正一反（形态变更须抽空而非猜），外加键/值双提取语义。"""
    failures: list[str] = []

    def ok(cond: bool, what: str) -> None:
        if not cond:
            failures.append(what)

    # 1) C# 字面量：注释行跳过、诊断语句抹白、普通字面量保留
    cs = (
        "// 注释里的 \"中文\" 不算\n"
        "var a = \"显示主窗\";\n"
        "HostLog.Write(\"[host] 写入失败：{ex.Message}\");\n"
        "report(new BootstrapProgress(step, \"下载中\"));\n"
    )
    lits = extract_cs_literals_scoped(cs)
    ok('"显示主窗"' in lits, f"C# 字面量应保留真文案（got={lits}）")
    ok(not any("中文" in x for x in lits), f"注释行不该进字面量（got={lits}）")
    ok(not any("写入失败" in x for x in lits), f"诊断调用应整语句抹白（got={lits}）")
    ok(not any("下载中" in x for x in lits), f"引导进度帧应整语句抹白（got={lits}）")

    # 2) 页面节点文本/键：<code> 嵌套保留、去标签
    html = '<b data-i18n="retry">重试 <code>dsh</code> 一下</b><i data-i18n-aria="capClose"></i>'
    ok(extract_i18n_node_text(html) == {"retry": "重试 dsh 一下"},
       f"节点文本应去标签保留 <code> 内容（got={extract_i18n_node_text(html)}）")
    ok(extract_i18n_keys(html) == {"retry", "capClose"},
       f"节点键应含文本键与可达名键（got={extract_i18n_keys(html)}）")

    # 3) CaptionButtonNames：取方法体内的三个 zh 值，不被后续方法体污染
    chrome = (
        "public static (string, string, string) CaptionButtonNames(UiLanguage language) => (\n"
        "    Pick(language, zh: \"最小化\", en: \"Minimize\"),\n"
        "    Pick(language, zh: \"最大化\", en: \"Maximize\"),\n"
        "    Pick(language, zh: \"关闭\", en: \"Close\"));\n"
        "public static string CaptionFullscreenName(UiLanguage language) => Pick(language, zh: \"全屏\", en: \"Full\");\n"
    )
    ok(extract_caption_zh_names(chrome) == {"capMinimize": "最小化", "capMaximize": "最大化", "capClose": "关闭"},
       f"应只取方法体内三个 zh 值（got={extract_caption_zh_names(chrome)}）")
    ok(extract_caption_zh_names("public static void X() { }") == {},
       "无签名时须返回空 dict（调用方 fail loud）")

    # 4) 静态标题栏可达名
    caps = extract_static_caption_labels(
        '<button data-webview-close aria-label="关闭" data-i18n-aria="capClose">x</button>')
    ok(caps == {"capClose": "关闭"}, f"可达名应取 aria-label（got={caps}）")

    # 5) `var D` 语言表：值内转义解码、注释抹白、多语言分块
    page = (
        "var D = {\n"
        "  en: {\n    retry: 'Retry',\n    sep: '\\u003a ',\n  },\n"
        "  ja: {\n    retry: '再試行',\n    // 注释里的 ghost: 'NOPE' 不算\n  },\n"
        "};\n"
    )
    tables = extract_html_locale_tables(page)
    ok(tables.get("en") == {"retry": "Retry", "sep": ": "}, f"表值应解码转义（got={tables.get('en')}）")
    ok(tables.get("ja") == {"retry": "再試行"}, f"注释不该成键（got={tables.get('ja')}）")
    # 反证夹具本身有效：注释若不被抹白就会多出 ghost 键，故上面那条能杀掉 _strip_js_comments 退化
    ok("NOPE" not in str(tables), f"注释内容不该出现在任何表里（got={tables}）")
    ok(extract_html_locale_tables("") == {}, "空输入须抽空（形态变更 fail loud）")

    # 6) client.js 键/值：裸键、引号键、双引号值；非字面量值不进值表
    js = (
        "var zh = {\n"
        "  label: 'x, fake: 1',\n"
        "  'quoted key': '值',\n"
        "  dyn: someConst,\n"
        "  dq: \"双引号\",\n"
        "}\n"
    )
    decl = client_decl("zh")
    keys = set(extract_client_keys(js, decl))
    ok({"label", "quoted key", "dyn", "dq"} <= keys, f"键集应同时收裸键与引号键（got={keys}）")
    # 反证：值里的 `, fake: 1` 若未被抹白就会成键，故上面那条能杀掉 _blank 退化
    ok("fake" not in keys, f"值内的形似键片段不得成键（got={keys}）")
    values = extract_client_values(js, decl)
    ok(values.get("label") == "x, fake: 1" and values.get("dq") == "双引号",
       f"值表应收单双引号字面量（got={values}）")
    ok("dyn" not in values, f"非字面量值不得进值表（got={values}）")
    ok(extract_client_values(js, client_decl("en")) == {}, "无声明时值表须为空")

    if failures:
        for f in failures:
            print(f"FAIL {f}")
        return 1
    print("ui_copy_extract: self-test OK (6 groups)")
    return 0


if __name__ == "__main__":
    sys.exit(_self_test())
