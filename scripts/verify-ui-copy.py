#!/usr/bin/env python3
"""verify-ui-copy — UI 文案单一词典门禁（批次 A，ADR review-to-machine-gates）。

词典 = src/DeepSeek.Harness.Desktop/Services/UiCopy.cs（唯一事实源）。校验两条不变量：
1. UI 消费文件（C#）不得再出现 CJK 字符串字面量——全部文案必须经 UiCopy；
2. wwwroot/index.html 的中文文案必须在 UiCopy.cs 登记过（静态页是被核对的消费方），
   覆盖两类形态：HTML 文本节点 + <script> 内引号字符串（运行时拼接进页面的文案）。

host.log 诊断行不属 UI 文案：字面量带 `[host]` 等 log 形态前缀的豁免（已知边界：
以 `[tag]` 开头的真 UI 文案理论上可借道，风险可忽略——UI 文案没有该书写惯例）。
自测：--self-test（临时夹具，正反四例）。
"""
from __future__ import annotations

import re
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "src" / "DeepSeek.Harness.Desktop"

UI_COPY = SRC / "Services" / "UiCopy.cs"

# UI 消费文件（相对 SRC）：这些文件里禁止出现 CJK 字符串字面量（文案一律走 UiCopy）。
# 新增 UI 消费文件时把文件加进此表并把文案迁入 UiCopy。
# 已知盲区：CS_LITERAL 不覆盖 @"..." 逐字串与 """...""" 原始串——消费文件引入该形态时须同步扩提取器。
CS_CONSUMERS = [
    "Services/Tray/TrayMenuActions.cs",
    "Services/Update/UpdateBanner.cs",
    "Services/UiLocale.cs",
    "Services/RecoveryPageBuilder.cs",
    "Services/DesktopBanner.cs",
    "Services/PagePump.cs",
]

INDEX_HTML = SRC / "wwwroot" / "index.html"

CJK = re.compile(r"[一-龥]")
# C# 字符串字面量（逐段粗提取足够：词典与消费文件均为普通 "..." 形态，无逐字字符串）
CS_LITERAL = re.compile(r'"(?:[^"\\\n]|\\.)*"')
# host.log 诊断行形态（[host]/[bootstrap]/… 前缀）：即使在 UI 消费文件里也是诊断而非 UI 文案，豁免
LOG_LINE = re.compile(r'^"\[[a-z-]+\]')
# HTML 处理：剥 <style>/<script> 后取标签间文本；script 块单独抽引号字符串
STYLE_SCRIPT = re.compile(r"<style[\s\S]*?</style>|<script[\s\S]*?</script>", re.IGNORECASE)
SCRIPT_BLOCK = re.compile(r"<script[^>]*>([\s\S]*?)</script>", re.IGNORECASE)
HTML_TEXT = re.compile(r">([^<>]+)<")
QUOTED = re.compile(r"'([^'\n]*)'|\"([^\"\n]*)\"")


def extract_cs_literals(text: str) -> list[str]:
    return CS_LITERAL.findall(text)


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


def _check(vocab_literals: list[str], cs_files: list[tuple[str, str]], html_text: str) -> list[str]:
    """两条不变量的唯一实现（真实仓库与 self-test 夹具共用，防双实现漂移）。"""
    failures: list[str] = []
    for name, text in cs_files:
        for lit in extract_cs_literals(text):
            if CJK.search(lit) and not LOG_LINE.match(lit):
                failures.append(f"{name}: CJK 字面量未走 UiCopy 词典：{lit}")
    for chunk in extract_html_text_chunks(html_text):
        if not any(chunk in lit for lit in vocab_literals):
            failures.append(f"wwwroot/index.html: 文本块未在 UiCopy.cs 登记：{chunk!r}")
    for lit in extract_html_script_strings(html_text):
        if not any(lit in v or v in lit for v in vocab_literals):
            failures.append(f"wwwroot/index.html: 脚本字符串未在 UiCopy.cs 登记：{lit!r}")
    return failures


def verify() -> list[str]:
    vocab_literals = extract_cs_literals(UI_COPY.read_text(encoding="utf-8"))
    cs_files = [(rel, (SRC / rel).read_text(encoding="utf-8")) for rel in CS_CONSUMERS]
    return _check(vocab_literals, cs_files, INDEX_HTML.read_text(encoding="utf-8"))


def self_test() -> int:
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)

        vocab = 'public static class UiCopy { public const string A = "显示主窗"; public const string B = "正在安装…"; }'
        html_ok = "<html><body><p>显示主窗</p><script>var x='正在安装…';</script></body></html>"
        html_bad_text = "<html><body><p>未登记文案</p></body></html>"
        html_bad_script = "<html><body><script>var y='未登记脚本串';</script></body></html>"

        v = tmp / "UiCopy.cs"
        v.write_text(vocab, encoding="utf-8")
        cs = tmp / "Tray.cs"

        # 反例 1：消费文件私藏 CJK 字面量
        cs.write_text('var label = english ? "Show" : "显示主窗";', encoding="utf-8")
        f1 = _check(extract_cs_literals(vocab), [("Tray.cs", cs.read_text(encoding="utf-8"))], html_ok)
        assert len(f1) == 1 and "UiCopy 词典" in f1[0], f1

        # 反例 2：HTML 文本块未登记
        cs.write_text("var label = UiCopy.A;", encoding="utf-8")
        f2 = _check(extract_cs_literals(vocab), [("Tray.cs", cs.read_text(encoding="utf-8"))], html_bad_text)
        assert len(f2) == 1 and "文本块" in f2[0], f2

        # 反例 3：脚本字符串未登记
        f3 = _check(extract_cs_literals(vocab), [("Tray.cs", cs.read_text(encoding="utf-8"))], html_bad_script)
        assert len(f3) == 1 and "脚本字符串" in f3[0], f3

        # 正例：词典收容一切（含 log 形态豁免）
        cs.write_text('HostLog.Write("[host] 写入失败：{ex.Message}");', encoding="utf-8")
        f4 = _check(extract_cs_literals(vocab), [("Tray.cs", cs.read_text(encoding="utf-8"))], html_ok)
        assert f4 == [], f4

    print("self-test OK (4 cases)")
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
