#!/usr/bin/env python3
"""verify-ui-copy — UI 文案单一词典门禁（批次 A，ADR review-to-machine-gates）。

词典 = src/DeepSeek.Harness.Desktop.Core/Localization/UiCopy.cs（唯一事实源）。校验四条不变量：
1. UI 消费文件（C#）不得再出现 CJK 字符串字面量——全部文案必须经 UiCopy；
   两处结构豁免：诊断调用行（`_log.Invoke`/`HostLog.Write` 等，`[tag]` 前缀按行判）；
   `new BootstrapProgress(step, "…")` 的进度行
   （引导进度只进诊断面，页面仅在失败时展示 fail 文案，故不入典）；
2. wwwroot/index.html 的中文文案必须在 UiCopy.cs 登记过（静态页是被核对的消费方），
   覆盖两类形态：HTML 文本节点 + <script> 内引号字符串（运行时拼接进页面的文案）；
3. index.html 的英文文案与 UiCopy.cs 双向对账（全等）：`var EN = { ... }` 里的英文串须
   与 UiCopy.cs 的某个字面量**全等**；成员名以 `En` 结尾的 `public const string`（`<Name>En`）
   的值也须与某个 EN 值全等（包含判定会放过「短串恰好落在长登记串里」的漏登记）；
4. plugins/dsh-desktop-companion/client/client.js 的 `var zh` / `var en` 两本字典键集相等
   （键漏一边即漏译）。

host.log 诊断行不属 UI 文案：字面量带 `[host]` 等 log 形态前缀的豁免（已知边界：
以 `[tag]` 开头的真 UI 文案理论上可借道，风险可忽略——UI 文案没有该书写惯例）。
不变量 3 的提取边界：`var EN = {` 须独占声明行，对象体到其后首个行首 `}`/`};` 为止
（扁平键值表，无嵌套对象）；比较前统一解码 `\\uXXXX` 等转义（姊妹文件惯用 `\\u2026` 写
省略号，两侧写法可能不同）。拼接串（`正在安装 ` + 插件名）登记的正是前段本身，故全等成立。
不变量 1 的提取边界：注释行（`//`/`///`/`*`/`/*` 起头）不参与提取——注释是散文，不是文案。
自测：--self-test（临时夹具，正反十例）。
"""
from __future__ import annotations

import re
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "src" / "DeepSeek.Harness.Desktop"
CORE = REPO / "src" / "DeepSeek.Harness.Desktop.Core"
INFRA = REPO / "src" / "DeepSeek.Harness.Desktop.Infrastructure"

# B1 起词典随 UiLocale 迁 Core（eShopOnWeb Core 亦有 Resources 先例）；UiCopy 是唯一事实源不变。
UI_COPY = CORE / "Localization" / "UiCopy.cs"

# UI 消费文件（相对各自工程根）：这些文件里禁止出现 CJK 字符串字面量（文案一律走 UiCopy）。
# 新增 UI 消费文件时把文件加进此表并把文案迁入 UiCopy。
# 已知盲区：CS_LITERAL 不覆盖 @"..." 逐字串与 """...""" 原始串——消费文件引入该形态时须同步扩提取器。
CS_CONSUMERS = [
    (SRC, "Tray/TrayMenuActions.cs"),
    (SRC, "Update/UpdateBanner.cs"),
    (CORE, "Localization/UiLocale.cs"),
    (SRC, "Recovery/RecoveryPageBuilder.cs"),
    (SRC, "PageBridge/DesktopBanner.cs"),
    (SRC, "PageBridge/PagePump.cs"),
    (SRC, "Tray/TrayCheckFeedback.cs"),
    (SRC, "Tray/DesktopTrayCommandRouter.cs"),
    (INFRA, "Runtime/RuntimeBootstrap.cs"),
    (INFRA, "Runtime/RuntimeBootstrap.Pure.cs"),
    (INFRA, "Runtime/RuntimeBootstrap.Engine.cs"),
    (INFRA, "Bootstrap/FirstBootBootstrapService.cs"),
]

INDEX_HTML = SRC / "wwwroot" / "index.html"

# 伴随插件客户端：zh/en 两本字典必须同键（不变量 4 的对账对象，不进 CS_CONSUMERS）。
CLIENT_JS = REPO / "plugins" / "dsh-desktop-companion" / "client" / "client.js"
CLIENT_JS_LABEL = "plugins/dsh-desktop-companion/client/client.js"

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
# HTML 处理：剥 <style>/<script> 后取标签间文本；script 块单独抽引号字符串
STYLE_SCRIPT = re.compile(r"<style[\s\S]*?</style>|<script[\s\S]*?</script>", re.IGNORECASE)
SCRIPT_BLOCK = re.compile(r"<script[^>]*>([\s\S]*?)</script>", re.IGNORECASE)
HTML_TEXT = re.compile(r">([^<>]+)<")
QUOTED = re.compile(r"'([^'\n]*)'|\"([^\"\n]*)\"")

# —— 不变量 3：index.html 的 `var EN = {` 扁平字典 ——
# 声明独占一行；收尾允许 `};` 与 `}`（姊妹文件 client.js 用的是无分号的 `}`）。
EN_DECL = re.compile(r"^[ \t]*var[ \t]+EN[ \t]*=[ \t]*\{[ \t]*$", re.MULTILINE)
OBJECT_END = re.compile(r"^[ \t]*\};?[ \t]*$", re.MULTILINE)
# JS 引号串（转义原文保留，比较前统一解码）；注释抹白，注释里的引号不参与提取
JS_NOISE = re.compile(r"'(?:\\.|[^'\\\n])*'|\"(?:\\.|[^\"\\\n])*\"|//[^\n]*|/\*[\s\S]*?\*/")
JS_STRING = re.compile(r"'(?:\\.|[^'\\\n])*'|\"(?:\\.|[^\"\\\n])*\"")
# 紧跟 `:` 的引号串是键（扁平表里值后面不会跟 `:`），不是 EN 文案
KEY_TAIL = re.compile(r"[ \t]*:")
# `public const string <Name>En = "...";`（值可跨行；成员名以 En 结尾即英文登记项）
CS_EN_CONST = re.compile(
    r"public\s+const\s+string\s+([A-Za-z_][A-Za-z0-9_]*En)\s*=\s*(\"(?:[^\"\\\n]|\\.)*\")\s*;"
)
ASCII_LETTER = re.compile(r"[A-Za-z]")

# —— 不变量 4：client.js 的 `var zh` / `var en` 字典 ——
CLIENT_ZH_DECL = re.compile(r"^[ \t]*var[ \t]+zh[ \t]*=[ \t]*\{[ \t]*$", re.MULTILINE)
CLIENT_EN_DECL = re.compile(r"^[ \t]*var[ \t]+en[ \t]*=[ \t]*\{[ \t]*$", re.MULTILINE)
# 键形态：裸标识符（值/注释先抹白，值里的 `, key:` 才不会误判成键）或引号键
JS_BARE_KEY = re.compile(r"(?:^|[,{\n])[ \t]*([A-Za-z_$][A-Za-z0-9_$]*)[ \t]*:")
JS_QUOTED_KEY = re.compile(r"(['\"])((?:\\.|(?!\1)[^\\\n])+)\1[ \t]*:")

_ESCAPES = {"n": "\n", "r": "\r", "t": "\t", "\\": "\\", "'": "'", '"': '"', "0": "\0"}


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


def extract_html_text_chunks(text: str) -> list[str]:
    body = STYLE_SCRIPT.sub("", text)
    chunks = []
    for m in HTML_TEXT.finditer(body):
        chunk = m.group(1).strip()
        if CJK.search(chunk):
            chunks.append(chunk)
    return chunks


def extract_html_script_strings(text: str) -> list[str]:
    out = []
    for block in SCRIPT_BLOCK.finditer(text):
        for m in QUOTED.finditer(block.group(1)):
            lit = m.group(1) if m.group(1) is not None else m.group(2)
            if CJK.search(lit):
                out.append(lit)
    return out


def extract_html_en_literals(text: str) -> list[str]:
    """抽 `var EN = { ... }` 对象体里的全部引号字面量（键与注释不计，转义已解码）。"""
    out = []
    for body in _object_bodies(text, EN_DECL):
        stripped = _strip_js_comments(body)
        for m in JS_STRING.finditer(stripped):
            if KEY_TAIL.match(stripped, m.end()):
                continue
            out.append(_unescape(m.group(0)[1:-1]))
    return out


def extract_en_consts(text: str) -> list[tuple[str, str]]:
    """抽 UiCopy.cs 的英文登记常量：`public const string <Name>En = "...";`。"""
    return [(m.group(1), _unescape(m.group(2)[1:-1])) for m in CS_EN_CONST.finditer(text)]


def extract_client_keys(text: str, decl: re.Pattern[str]) -> list[str]:
    """抽 `var zh`/`var en` 字典的键集（裸标识符与引号键；行尾注释不影响）。"""
    keys = []
    for body in _object_bodies(text, decl):
        keys += [m.group(1) for m in JS_BARE_KEY.finditer(JS_NOISE.sub(_blank, body))]
        keys += [_unescape(m.group(2)) for m in JS_QUOTED_KEY.finditer(_strip_js_comments(body))]
    return keys


def _check(
    copy_text: str,
    cs_files: list[tuple[str, str]],
    html_text: str,
    client_text: str,
) -> list[str]:
    """四条不变量的唯一实现（真实仓库与 self-test 夹具共用，防双实现漂移）。

    夹具走文本入参（既有形态：写临时文件再读文本）；消息里的位置标签用生产常量。
    """
    failures: list[str] = []
    index_label = "wwwroot/index.html"
    copy_label = "UiCopy.cs"
    client_label = CLIENT_JS_LABEL
    vocab_literals = extract_cs_literals_scoped(copy_text)

    # 不变量 1：消费文件零 CJK 字面量（注释、诊断调用、引导进度帧三处豁免）
    for name, text in cs_files:
        for lit in extract_cs_literals_scoped(text):
            if CJK.search(lit) and not LOG_LINE.match(lit):
                failures.append(f"{name}: CJK 字面量未走 UiCopy 词典：{lit}")

    # 不变量 2：index.html 中文文案登记（语义与既往一致，不放松）
    for chunk in extract_html_text_chunks(html_text):
        if not any(chunk in lit for lit in vocab_literals):
            failures.append(f"{index_label}: 文本块未在 UiCopy.cs 登记：{chunk!r}")
    for lit in extract_html_script_strings(html_text):
        if not any(lit in v or v in lit for v in vocab_literals):
            failures.append(f"{index_label}: 脚本字符串未在 UiCopy.cs 登记：{lit!r}")

    # 不变量 3：index.html 英文文案 ⇄ UiCopy.cs 双向对账（全等：短串落在长串里不算登记）
    cs_contents = {_unescape(lit[1:-1]) for lit in vocab_literals}
    en_literals = [v for v in extract_html_en_literals(html_text) if ASCII_LETTER.search(v) and not CJK.search(v)]
    en_values = set(en_literals)
    for value in en_literals:
        if value not in cs_contents:
            failures.append(f"{index_label}: EN 文案未在 UiCopy.cs 登记：{value!r}")
    for name, value in extract_en_consts(copy_text):
        if value not in en_values:
            failures.append(f"{copy_label}: {name} 未在 index.html 出现：{value!r}")

    # 不变量 4：client.js zh/en 字典同键（独立于消费清单逻辑）
    zh_keys = set(extract_client_keys(client_text, CLIENT_ZH_DECL))
    en_keys = set(extract_client_keys(client_text, CLIENT_EN_DECL))
    for var, keys in (("var zh", zh_keys), ("var en", en_keys)):
        if not keys:
            failures.append(f"{client_label}: 未能从 {var} 提取到键（声明形态变更？）")
    if zh_keys and en_keys:
        if missing := sorted(zh_keys - en_keys):
            failures.append(f"{client_label}: en 缺少键：{missing!r}")
        if missing := sorted(en_keys - zh_keys):
            failures.append(f"{client_label}: zh 缺少键：{missing!r}")
    return failures


def verify() -> list[str]:
    cs_files = [((base.name + "/" + rel), (base / rel).read_text(encoding="utf-8")) for base, rel in CS_CONSUMERS]
    return _check(
        UI_COPY.read_text(encoding="utf-8"),
        cs_files,
        INDEX_HTML.read_text(encoding="utf-8"),
        CLIENT_JS.read_text(encoding="utf-8"),
    )


def self_test() -> int:
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)

        vocab = 'public static class UiCopy { public const string A = "显示主窗"; public const string B = "正在安装…"; }'
        html_ok = "<html><body><p>显示主窗</p><script>var x='正在安装…';</script></body></html>"
        html_bad_text = "<html><body><p>未登记文案</p></body></html>"
        html_bad_script = "<html><body><script>var y='未登记脚本串';</script></body></html>"
        # 两本字典同键（收尾用无分号 `}`，与 client.js 现行写法一致）
        client_ok = "var zh = {\n  alpha: '甲',\n}\nvar en = {\n  alpha: 'A',\n}\n"

        v = tmp / "UiCopy.cs"
        v.write_text(vocab, encoding="utf-8")
        cs = tmp / "Tray.cs"

        # 反例 1：消费文件私藏 CJK 字面量
        cs.write_text('var label = english ? "Show" : "显示主窗";', encoding="utf-8")
        f1 = _check(vocab, [("Tray.cs", cs.read_text(encoding="utf-8"))], html_ok, client_ok)
        assert len(f1) == 1 and "UiCopy 词典" in f1[0], f1

        # 反例 2：HTML 文本块未登记
        cs.write_text("var label = UiCopy.A;", encoding="utf-8")
        f2 = _check(vocab, [("Tray.cs", cs.read_text(encoding="utf-8"))], html_bad_text, client_ok)
        assert len(f2) == 1 and "文本块" in f2[0], f2

        # 反例 3：脚本字符串未登记
        f3 = _check(vocab, [("Tray.cs", cs.read_text(encoding="utf-8"))], html_bad_script, client_ok)
        assert len(f3) == 1 and "脚本字符串" in f3[0], f3

        # 正例：词典收容一切（含 log 形态豁免）
        cs.write_text('HostLog.Write("[host] 写入失败：{ex.Message}");', encoding="utf-8")
        f4 = _check(vocab, [("Tray.cs", cs.read_text(encoding="utf-8"))], html_ok, client_ok)
        assert f4 == [], f4

        # 反例 5：index.html 的 EN 文案未在 UiCopy.cs 登记
        vocab_en = 'public static class UiCopy { public const string OkEn = "OK"; }'
        html_en_bad = "<html><body><script>\nvar EN = {\n  ok: 'OK',\n  ghost: 'Brand New Copy',\n};\n</script></body></html>"
        f5 = _check(vocab_en, [], html_en_bad, client_ok)
        assert len(f5) == 1 and "EN 文案未在 UiCopy.cs 登记" in f5[0] and "Brand New Copy" in f5[0], f5

        # 反例 6：UiCopy 的 En 常量未在 index.html 出现（登记方向）
        vocab_ghost = (
            'public static class UiCopy { public const string OkEn = "OK"; '
            'public const string GhostEn = "Registered But Absent"; }'
        )
        html_en_ok = "<html><body><script>\nvar EN = {\n  ok: 'OK',\n};\n</script></body></html>"
        f6 = _check(vocab_ghost, [], html_en_ok, client_ok)
        assert len(f6) == 1 and "GhostEn 未在 index.html 出现" in f6[0] and "Registered But Absent" in f6[0], f6

        # 反例 7：client.js 两本字典键不一致（含引号键与行尾注释）
        client_bad = (
            "var zh = {\n"
            "  alpha: '\\u7532',\n"
            "  'beta': '乙', // 行尾注释\n"
            "};\n"
            "var en = {\n"
            '  "alpha": \'A\',\n'
            "  gamma: 'G',\n"
            "};\n"
        )
        f7 = _check(vocab, [], html_ok, client_bad)
        assert len(f7) == 2, f7
        assert "en 缺少键：['beta']" in f7[0] and "zh 缺少键：['gamma']" in f7[1], f7

        # 正例 8：注释行与引导进度行的 CJK 均豁免，同一文件里的真 UI 文案仍拦
        cs.write_text(
            '// 注释里的 "中文引号" 不是文案\n'
            'report(new BootstrapProgress(BootstrapStep.EnsureNode, "检测系统全局 Node"));\n'
            '_log.Invoke($"[host] 前缀 {string.Join(", ", xs)}，诊断尾巴");\n'
            'var label = "未入典文案";',
            encoding="utf-8")
        f8 = _check(vocab, [("Tray.cs", cs.read_text(encoding="utf-8"))], html_ok, client_ok)
        assert len(f8) == 1 and "未入典文案" in f8[0], f8

        # 反例 9：EN 短串恰好落在长登记串里——全等判定须拦（包含判定会漏）
        vocab_long = 'public static class UiCopy { public const string A = "Optional plugins skipped"; }'
        html_en_substring = (
            "<html><body><script>\nvar EN = {\n  optionalPluginsTitle: 'Optional plugins',\n};\n"
            "</script></body></html>")
        f9 = _check(vocab_long, [], html_en_substring, client_ok)
        assert len(f9) == 1 and "EN 文案未在 UiCopy.cs 登记" in f9[0], f9

        # 反例 10：En 常量与 EN 值只是包含关系（非全等）——登记方向须拦
        vocab_contain = 'public static class UiCopy { public const string SkipEn = "Plugin install skipped"; }'
        html_en_contain = (
            "<html><body><script>\nvar EN = {\n  preinstallSkipped: 'Plugin install skipped (zh)',\n};\n"
            "</script></body></html>")
        f10 = _check(vocab_contain, [], html_en_contain, client_ok)
        assert any("SkipEn 未在 index.html 出现" in x for x in f10), f10

    print("self-test OK (10 cases)")
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
