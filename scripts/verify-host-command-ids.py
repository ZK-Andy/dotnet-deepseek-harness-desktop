#!/usr/bin/env python3
"""Verify host↔page command IDs have a single source of truth (ADR host-command-ids).

The command names the shell serves and the companion plugin / guide page calls
(`desktop.update.getState`, `app.openExternal`, …) are a wire contract. Before
this gate they were hard-coded on both sides, so renaming one side silently
disabled the other — no compiler error, no test failure, just a dead button.

Three rules (the single source of truth is `src/DeepSeek.Harness.Desktop.Core/
Ipc/HostCommandIds.cs`):

  A. no known command ID may appear as a *whole* string literal in C# outside
     the constants file — the shell and the tests must reference the constant.
     A literal that merely *contains* an ID (a JSON payload, a log assertion on
     a script body) is not a command reference and stays legal.
  B. every command-ID-looking literal in the companion plugin JS and in the
     shell's own wwwroot HTML must be a known ID — the plugin side may not
     invent a command the shell does not serve.
  C. every known ID must be referenced somewhere (rule A's constant usage, or a
     JS/HTML literal) — a constant nobody uses is a stale contract.

Scanning notes (deliberately conservative, a gate scanner not a C# lexer):
comment-only lines are skipped via `gate_common.CSharpLineScanner`; string
literals are matched whole, so `"desktop.update.getState"` is a reference but
`"{\"scope\":\"desktop.update.getState\"}"` is data. The JS/HTML scan strips
comments with a small string-aware scanner before looking for literals.

Usage:
    python3 scripts/verify-host-command-ids.py [--repo ROOT] [--enforce]
    python3 scripts/verify-host-command-ids.py --self-test
Exit code 0 = pass, 1 = violations.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

from gate_common import SelfTest, repo_root

CONSTANTS_REL = "src/DeepSeek.Harness.Desktop.Core/Ipc/HostCommandIds.cs"
COMPANION_REL = "plugins/dsh-desktop-companion"
WWWROOT_REL = "src/DeepSeek.Harness.Desktop/wwwroot"

CONST_RE = re.compile(r'public const string (?P<name>\w+) = "(?P<value>[^"]+)";')
# 命令 ID 形态：<前缀>.<段>.<段>…（段首字母）。文件名形态（<前缀>.<段>.<扩展名>）不属命令 ID——
# 扩展名段排除 .yml/.yaml/.json/.md/.txt/.log/.png/.ico/.svg/.html/.js/.css（仓内只有 desktop.cordis.yml 一例）。
ID_RE = re.compile(
    r"(?:desktop|app)\.[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)*")
ID_FILE_SUFFIX_RE = re.compile(r"\.(?:yml|yaml|json|md|txt|log|png|ico|svg|html|js|css)$")


def parse_constants(path: Path) -> dict[str, str]:
    """Map constant name -> command ID from the Core constants file."""
    out: dict[str, str] = {}
    for m in CONST_RE.finditer(path.read_text(encoding="utf-8")):
        out[m.group("name")] = m.group("value")
    return out


def strip_js_comments(text: str) -> str:
    """Blank out JS/HTML comments while keeping string literals intact."""
    out: list[str] = []
    i, n = 0, len(text)
    quote = ""
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if quote:
            out.append(c)
            if c == "\\" and nxt:
                out.append(nxt)
                i += 2
                continue
            if c == quote:
                quote = ""
            i += 1
            continue
        if c in "\"'`":
            quote = c
            out.append(c)
            i += 1
            continue
        if c == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                out.append(" ")
                i += 1
            continue
        if c == "/" and nxt == "*":
            while i < n and not (text[i] == "*" and i + 1 < n and text[i + 1] == "/"):
                out.append("\n" if text[i] == "\n" else " ")
                i += 1
            out.append("  ")
            i += 2
            continue
        if text.startswith("<!--", i):
            while i < n and not text.startswith("-->", i):
                out.append("\n" if text[i] == "\n" else " ")
                i += 1
            out.append("   ")
            i += 3
            continue
        out.append(c)
        i += 1
    return "".join(out)


def strip_trailing_comment(line: str) -> str:
    """Drop a trailing `//` comment, keeping `//` inside string literals (URLs)."""
    in_string = False
    i = 0
    while i < len(line) - 1:
        c = line[i]
        if c == '"' and (i == 0 or line[i - 1] != '\\'):
            in_string = not in_string
        elif not in_string and c == "/" and line[i + 1] == "/":
            return line[:i]
        i += 1
    return line


def csharp_literals(text: str) -> list[tuple[int, str]]:
    """Whole string literals with their line number (comments excluded)."""
    from gate_common import CSharpLineScanner  # local import: keeps the module import graph flat

    found: list[tuple[int, str]] = []
    lines = text.splitlines()
    cleaned = list(CSharpLineScanner(lines).iter_cleaned())
    for idx, (raw, cl) in enumerate(zip(lines, cleaned)):
        if cl.strip() == "":
            continue  # comment-only line
        code = strip_trailing_comment(raw)
        for m in re.finditer(r'"((?:[^"\\\n]|\\.)*)"', code):
            found.append((idx + 1, m.group(1)))
    return found


def csharp_code_lines(text: str) -> list[str]:
    """Code lines with comments removed (for constant-reference matching)."""
    from gate_common import CSharpLineScanner  # local import: keeps the module import graph flat

    out: list[str] = []
    lines = text.splitlines()
    cleaned = list(CSharpLineScanner(lines).iter_cleaned())
    for raw, cl in zip(lines, cleaned):
        out.append(cl if cl.strip() else "")
    return out


def scan(repo: Path) -> list[str]:
    violations: list[str] = []
    constants_path = repo / CONSTANTS_REL
    if not constants_path.is_file():
        return [f"命令 ID 单一事实源文件不存在: {CONSTANTS_REL}"]

    consts = parse_constants(constants_path)
    if not consts:
        return [f"{CONSTANTS_REL} 未解析出任何 public const string（判据失效，fail loud）"]
    known_values = set(consts.values())
    known_names = set(consts)

    # Rule A + C(partial): C# 侧不得出现已知 ID 的整串字面量；常量须被引用。
    used_names: set[str] = set()
    for path in sorted((repo / "src").rglob("*.cs")) + sorted((repo / "tests").rglob("*.cs")):
        if "/obj/" in str(path) or "/bin/" in str(path) or path == constants_path:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for lineno, literal in csharp_literals(text):
            if literal in known_values:
                rel = path.relative_to(repo)
                violations.append(
                    f"{rel}:{lineno}: 命令 ID 字面量 \"{literal}\" 应改用 HostCommandIds 常量"
                    f"（单一事实源 {CONSTANTS_REL}）")
        code_lines = csharp_code_lines(text)
        for name in known_names:
            # 词边界匹配：`UpdateCheck` 不被 `UpdateCheckAll` 吞并；注释里的提及不算引用
            if re.search(rf"\bHostCommandIds\.{re.escape(name)}\b", "\n".join(code_lines)):
                used_names.add(name)

    # Rule B + C(partial): 插件侧与引导页的命令 ID 必须在已知集合内。
    js_ids: set[str] = set()
    scan_roots = [repo / COMPANION_REL, repo / WWWROOT_REL]
    for root in scan_roots:
        if not root.is_dir():
            violations.append(f"扫描面目录不存在: {root.relative_to(repo)}")
            continue
        for path in sorted(root.rglob("*")):
            if not path.is_file() or path.suffix not in {".js", ".html"}:
                continue
            if "node_modules" in path.parts:
                continue
            text = path.read_text(encoding="utf-8", errors="replace")
            cleaned = strip_js_comments(text)
            rel = path.relative_to(repo)
            for m in ID_RE.finditer(cleaned):
                value = m.group(0)
                if ID_FILE_SUFFIX_RE.search(value):
                    continue  # 文件名形态不是命令 ID
                js_ids.add(value)
                if value not in known_values:
                    line = cleaned.count("\n", 0, m.start()) + 1
                    violations.append(
                        f"{rel}:{line}: 命令 ID \"{value}\" 不在已知集合内"
                        f"（{CONSTANTS_REL} 未定义——插件侧不得自造命令）")

    # Rule C: 每个已知 ID 都要有引用面（壳侧常量用法或插件/引导页字面量）。
    for name, value in consts.items():
        if name not in used_names and value not in js_ids:
            violations.append(
                f"{CONSTANTS_REL}: 常量 {name}（{value}）无任何引用面——死契约，删或接线")

    return violations


def _self_test() -> int:
    st = SelfTest()
    import tempfile

    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        (root / CONSTANTS_REL).parent.mkdir(parents=True, exist_ok=True)
        (root / CONSTANTS_REL).write_text(
            "# Agent Note: x\n\n"
            "public static class HostCommandIds\n{\n"
            "    public const string Ping = \"desktop.ping\";\n"
            "    public const string Pong = \"app.pong\";\n"
            "}\n",
            encoding="utf-8")
        (root / "src" / "App").mkdir(parents=True, exist_ok=True)
        (root / COMPANION_REL).mkdir(parents=True, exist_ok=True)
        (root / WWWROOT_REL).mkdir(parents=True, exist_ok=True)

        # 1) 合规：常量引用 + 插件字面量在已知集合内
        (root / "src" / "App" / "Router.cs").write_text(
            "using DeepSeek.Harness.Desktop.Core.Ipc;\n"
            "public sealed class Router\n{\n"
            "    public const string Name = HostCommandIds.Ping;\n"
            "    public const string Other = HostCommandIds.Pong;\n"
            "}\n",
            encoding="utf-8")
        (root / COMPANION_REL / "client.js").write_text(
            "// 注释里的 desktop.ping 不算引用\n"
            "invoke('desktop.ping')\n"
            "invoke('app.pong')\n",
            encoding="utf-8")
        st.ok(scan(root) == [], "合规样例通过")

        # 2) 壳侧又写了一遍已知 ID 的整串字面量
        (root / "src" / "App" / "Router.cs").write_text(
            "public sealed class Router\n{\n"
            "    public const string Name = \"desktop.ping\";\n"
            "    public const string Other = HostCommandIds.Pong;\n"
            "}\n",
            encoding="utf-8")
        rows = scan(root)
        st.ok(any("应改用 HostCommandIds 常量" in r for r in rows),
               "壳侧整串字面量判红")

        # 3) 插件侧自造命令
        (root / "src" / "App" / "Router.cs").write_text(
            "public sealed class Router\n{\n"
            "    public const string Name = HostCommandIds.Ping;\n"
            "    public const string Other = HostCommandIds.Pong;\n"
            "}\n",
            encoding="utf-8")
        (root / COMPANION_REL / "client.js").write_text(
            "invoke('desktop.unknown')\ninvoke('app.pong')\n", encoding="utf-8")
        rows = scan(root)
        st.ok(any("不在已知集合内" in r for r in rows), "插件侧未知命令判红")

        # 4) 注释里的 ID 不算引用（不得据此判「死契约」）：壳侧也不引用常量
        (root / "src" / "App" / "Router.cs").write_text(
            "public sealed class Router\n{\n}\n", encoding="utf-8")
        (root / COMPANION_REL / "client.js").write_text(
            "// desktop.ping\n// app.pong\n", encoding="utf-8")
        rows = scan(root)
        st.ok(any("无任何引用面" in r for r in rows), "注释里的 ID 不算引用面")

        # 5) C# 注释里的常量名不算引用面（Rule C 剥注释）
        (root / "src" / "App" / "Router.cs").write_text(
            "using DeepSeek.Harness.Desktop.Core.Ipc;\n"
            "public sealed class Router\n{\n"
            "    // 旧写法：HostCommandIds.Ping（注释不算引用）\n"
            "    public const string Name = \"desktop.ping\";\n"
            "}\n",
            encoding="utf-8")
        rows = scan(root)
        # 断言点名 Ping：若 Rule C 退回裸子串匹配（注释里的常量名被算作引用），
        # Ping 的那一条消失而 Pong 的仍在——只查「有无无引用面」会假绿。
        st.ok(any("Ping" in r and "无任何引用面" in r for r in rows),
               "C# 注释里的常量名不算引用面")

        # 6) 行尾注释里的带引号 ID 不算复写（strip_trailing_comment 的回归锁）
        (root / "src" / "App" / "Router.cs").write_text(
            "using DeepSeek.Harness.Desktop.Core.Ipc;\n"
            "public sealed class Router\n{\n"
            "    public const string Name = HostCommandIds.Ping;\n"
            "} // 旧名 \"desktop.ping\"（注释里的字面量不算复写）\n",
            encoding="utf-8")
        (root / COMPANION_REL / "client.js").write_text(
            "invoke('desktop.ping')\ninvoke('app.pong')\n", encoding="utf-8")
        rows = scan(root)
        st.ok(not any("应改用 HostCommandIds 常量" in r for r in rows),
               "行尾注释里的带引号 ID 不判红")

        # 5) 数据串（内含 ID 的 JSON）不判红
        (root / "src" / "App" / "Router.cs").write_text(
            "public sealed class Router\n{\n"
            "    public const string Payload = \"{\\\"scope\\\":\\\"desktop.ping\\\"}\";\n"
            "    public const string Name = HostCommandIds.Ping;\n"
            "    public const string Other = HostCommandIds.Pong;\n"
            "}\n",
            encoding="utf-8")
        (root / COMPANION_REL / "client.js").write_text(
            "invoke('desktop.ping')\ninvoke('app.pong')\n", encoding="utf-8")
        st.ok(scan(root) == [], "数据串内含 ID 不判红")

    return st.finish("verify-host-command-ids")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=".", help="repo root (default cwd)")
    parser.add_argument("--enforce", action="store_true", help="exit 1 when violations exist")
    parser.add_argument("--self-test", action="store_true", help="run offline fixtures")
    args = parser.parse_args()
    if args.self_test:
        return _self_test()

    repo = Path(args.repo).resolve()
    rows = scan(repo)
    if rows:
        print(f"host-command-ids: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        return 1 if args.enforce else 0
    print("host-command-ids: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
