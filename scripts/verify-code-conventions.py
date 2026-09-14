#!/usr/bin/env python3
"""Verify code conventions that need a comment-aware scanner (D004/D005).

Implements the contract-scan channel from the architecture-mechanization ADR
(`.agents/notes/proposed/process/2026-08-30-architecture-mechanization.md`).
Unlike raw `grep`, this scanner strips comments, so a `Process.Start` inside a
`///` doc comment or a `//` note does not count. D001–D003 stay with human
review (needs Roslyn semantics); only D004/D005 are machine-scanned.

  D004  log not via HostLog — `System.Console.WriteLine`/`Console.Write` in a
        file other than the whitelist (HostLog, compose-root diagnostics).
  D005  non-boundary layer calls external infrastructure directly — `Process`/
        `HttpClient`/`File.`/`Directory.`/`FileStream` in a file that is not
        a boundary (infrastructure) component. Boundary files are whitelisted.

`Path.` is deliberately NOT scanned: building paths is ubiquitous and harmless;
the boundary concern is heavier infrastructure (spawn/network/filesystem).

Default is report-only (exit 0). `--enforce` exits 1. `--self-test` runs
offline fixtures.

Usage: python3 scripts/verify-code-conventions.py [--enforce]
       python3 scripts/verify-code-conventions.py --self-test
"""

import argparse
import re
import sys
from pathlib import Path

DEFAULT_SRC = (
    "src/DeepSeek.Harness.Desktop",
    "src/DeepSeek.Harness.Desktop.Core",
    "src/DeepSeek.Harness.Desktop.Infrastructure",
)
IGNORE_MARK = "verify-code-conventions: ignore"

# Console use is allowed only in the log sink and the entry diagnostics.
D004_WHITELIST = {"HostLog.cs", "Program.cs"}

# Console use is allowed only in the log sink and the entry diagnostics.
D004_WHITELIST = {"HostLog.cs", "Program.cs"}

# B4 白名单退役（ADR official-clean-architecture-adoption）：物理分层后 D005 的豁免
# 按工程推导——Infrastructure 整工程豁免（边界层本体，适配器定义上直触外部世界）；
# 主工程（Presentation/组合根）与 Core 零白名单。Core 仅保留 B1 既定的两个只读
# profile/package 边界文件（ADR official-clean-architecture-adoption B1 批内台账）。
D005_PROJECT_EXEMPT = "DeepSeek.Harness.Desktop.Infrastructure"
D005_CORE_WHITELIST = {
    # profile package.json presence/version reads (B1 Core 边界; ADR official-clean-architecture-adoption)
    "ProfilePackageCheck.cs",
    "PluginVersionCheck.cs",
}

D004_RE = re.compile(r"Console\.(?:Write|WriteLine|Error\.Write)")
# Only actual construction/operation forms count — a bare type mention of
# ProcessStartInfo/HttpClient (e.g. as a parameter type passed between
# components) is not "直调外部基础设施".
D005_RE = re.compile(
    r"\b(?:Process\.Start|new Process\b|new ProcessStartInfo|new HttpClient\b|"
    r"File\.|Directory\.|FileStream)\b")

def _strip_comments_line(line: str, in_block: list[bool]) -> str:
    """Return the code-only text of a line, tracking block-comment state."""
    out: list[str] = []
    i = 0
    n = len(line)
    while i < n:
        c = line[i]
        nxt = line[i + 1] if i + 1 < n else ""
        if in_block[0]:
            if c == "*" and nxt == "/":
                in_block[0] = False
                i += 2
                continue
            i += 1
            continue
        if c == "/" and nxt == "/":
            break  # line comment
        if c == "/" and nxt == "*":
            in_block[0] = True
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def _file_is_allowed_d005(project: str, rel: Path) -> bool:
    """B4 退役形态：豁免按工程推导——Infrastructure 整工程豁免（边界层本体）；
    Core 仅两个只读边界文件；主工程（Presentation/组合根）零豁免。"""
    if project == D005_PROJECT_EXEMPT or project.endswith("." + D005_PROJECT_EXEMPT):
        return True
    if project == "DeepSeek.Harness.Desktop.Core" or project.endswith(".DeepSeek.Harness.Desktop.Core"):
        return rel.name in D005_CORE_WHITELIST
    return False


def _violations(abspath: Path, rel: Path, project: str) -> list[str]:
    text = abspath.read_text(encoding="utf-8")
    in_block = [False]
    out: list[str] = []
    d004_hits = 0
    d005_hits = 0
    for lineno, raw in enumerate(text.splitlines(), 1):
        code = _strip_comments_line(raw, in_block)
        if IGNORE_MARK in code or IGNORE_MARK in raw:
            continue
        if D004_RE.search(code):
            d004_hits += 1
        if D005_RE.search(code):
            d005_hits += 1

    if d004_hits and rel.name not in D004_WHITELIST:
        out.append(f"  {rel}: D004 Console used {d004_hits}x (log must go via HostLog)")
    if d005_hits and not _file_is_allowed_d005(project, rel):
        out.append(f"  {rel}: D005 infra used {d005_hits}x (Process/HttpClient/File/Directory/FileStream in non-boundary)")
    return out


def _scan(src: Path) -> list[str]:
    rows: list[str] = []
    project = src.name
    for path in sorted(src.rglob("*.cs")):
        rel = path.relative_to(src)
        if any(part in ("obj", "bin") for part in rel.parts):
            continue
        rows.extend(_violations(path, rel, project))
    return rows


def _self_test() -> int:
    import tempfile

    failed = 0
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        # D004: a doc-comment mention of Console must NOT count.
        (root / "DocOnly.cs").write_text(
            "/// <summary>uses Console.WriteLine in docs.</summary>\n"
            "public class A { public void M() { } }\n", encoding="utf-8")
        # HostLog (whitelisted) may use Console.
        (root / "HostLog.cs").write_text(
            "public static class HostLog { public static void Write(string m) { Console.WriteLine(m); } }\n",
            encoding="utf-8")
        inf = _scan(root)
        if any("D004" in r for r in inf):
            print(f"  ✗ doc-comment Console flagged: {inf}")
            failed = 1
        else:
            print("  ok: doc-comment Console not flagged; HostLog whitelisted")

        # D005（B4 退役形态）：主工程纯逻辑 File. -> flagged；Infrastructure 整工程豁免。
        app = root / "DeepSeek.Harness.Desktop"
        infra_proj = root / "DeepSeek.Harness.Desktop.Infrastructure"
        app.mkdir()
        infra_proj.mkdir()
        (app / "UpdateStateMachine.cs").write_text(
            "public class UpdateStateMachine { bool Has(string p) => File.Exists(p); }\n",
            encoding="utf-8")
        (infra_proj / "HarnessRuntimeHost.cs").write_text(
            "public class H { void M() { var p = new ProcessStartInfo(); } }\n",
            encoding="utf-8")
        inf = _scan(app) + _scan(infra_proj)
        if any("UpdateStateMachine.cs" in r and "D005" in r for r in inf):
            print("  ok: D005 flagged presentation File. use")
        else:
            print(f"  ✗ D005 not flagged: {inf}")
            failed = 1
        if not any("HarnessRuntimeHost.cs" in r for r in inf):
            print("  ok: Infrastructure project exempt as boundary layer")
        else:
            print(f"  ✗ infra file flagged: {inf}")
            failed = 1

        # ignore marker on the same line suppresses that line's D005.
        (root / "Ignored.cs").write_text(
            "public class I { bool Has(string p) => File.Exists(p); } // verify-code-conventions: ignore 组合根装配：配置面\n",
            encoding="utf-8")
        inf = _scan(root)
        if not any("Ignored.cs" in r for r in inf):
            print("  ok: D005 ignore marker suppresses a line")
        else:
            print(f"  ✗ ignore marker not honored: {inf}")
            failed = 1

    if failed == 0:
        print("== verify-code-conventions self-test passed ==")
    else:
        print("== verify-code-conventions self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(description="Verify D004/D005 code conventions")
    parser.add_argument("--src", nargs="+", default=list(DEFAULT_SRC))
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any violation (default: report only)")
    args = parser.parse_args()

    rows: list[str] = []
    for s in args.src:
        rows.extend(_scan(Path(s)))
    if rows:
        print(f"code-conventions: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        if args.enforce:
            return 1
    else:
        print("code-conventions: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
