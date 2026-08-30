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

DEFAULT_SRC = "src/DeepSeek.Harness.Desktop"
IGNORE_MARK = "verify-code-conventions: ignore"

# Console use is allowed only in the log sink and the entry diagnostics.
D004_WHITELIST = {"HostLog.cs", "Program.cs"}

# Boundary (infrastructure) components that legitimately talk to the external
# world. Files not here are treated as application/domain or compose-root and
# must NOT directly use the scanned infrastructure primitives.
D005_WHITELIST = {
    "HarnessRuntimeHost.cs",
    "RuntimeBootstrap.cs",
    "RuntimeLocator.cs",
    "RuntimeVersionGate.cs",
    "MarketInstallHelper.cs",
    "SystemBrowser.cs",
    "LauncherActivation.cs",
    "HostLog.cs",
    "RunMarker.cs",
    "Autostart.cs",
    "OrphanDshReaper.cs",
    "DiagnosticsExporter.cs",
    "PluginVersionCheck.cs",
    "DesktopProfileBootstrap.cs",
    "DevEnvironment.cs",
    "RuntimeBootstrapOptions.cs",
    "ExternalLinkCommandRouter.cs",
    # CLI shim plumbing
    "CliShimBuilder.cs",
    "CliShimPlanner.cs",
    "CliShimPath.cs",
    "CliShimRegistrar.cs",
    # process-spawn boundary (extracted from compose root, ADR 组合根只装配)
    "PluginProcessRunner.cs",
    # settings/legacy persistence boundary (read-only file I/O)
    "LegacyHomeNotice.cs",
    "CloseBehaviorPreference.cs",
}

D004_RE = re.compile(r"Console\.(?:Write|WriteLine|Error\.Write)")
# Only actual construction/operation forms count — a bare type mention of
# ProcessStartInfo/HttpClient (e.g. as a parameter type passed between
# components) is not "直调外部基础设施".
D005_RE = re.compile(
    r"\b(?:Process\.Start|new Process\b|new ProcessStartInfo|new HttpClient\b|"
    r"File\.|Directory\.|FileStream)\b")

# A /path/to/Services/Update/Anything.cs is an Update sub-domain boundary.
UPDATE_SUBDIR = "/Update/"


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


def _file_is_allowed_d005(rel: Path) -> bool:
    if str(rel).startswith("Services/Update/"):
        return True
    return rel.name in D005_WHITELIST


def _violations(abspath: Path, rel: Path) -> list[str]:
    text = abspath.read_text(encoding="utf-8")
    in_block = [False]
    out: list[str] = []
    d004_hits = 0
    d005_hits = 0
    for lineno, raw in enumerate(text.splitlines(), 1):
        code = _strip_comments_line(raw, in_block)
        if IGNORE_MARK in code:
            continue
        if D004_RE.search(code):
            d004_hits += 1
        if D005_RE.search(code):
            d005_hits += 1

    if d004_hits and rel.name not in D004_WHITELIST:
        out.append(f"  {rel}: D004 Console used {d004_hits}x (log must go via HostLog)")
    if d005_hits and not _file_is_allowed_d005(rel):
        out.append(f"  {rel}: D005 infra used {d005_hits}x (Process/HttpClient/File/Directory/FileStream in non-boundary)")
    return out


def _scan(src: Path) -> list[str]:
    rows: list[str] = []
    for path in sorted(src.rglob("*.cs")):
        rel = path.relative_to(src)
        if any(part in ("obj", "bin") for part in rel.parts):
            continue
        rows.extend(_violations(path, rel))
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

        # D005: pure-logic file using File. -> flagged; infra file ok.
        (root / "UpdateStateMachine.cs").write_text(
            "public class UpdateStateMachine { bool Has(string p) => File.Exists(p); }\n",
            encoding="utf-8")
        (root / "HarnessRuntimeHost.cs").write_text(
            "public class H { void M() { var p = new ProcessStartInfo(); } }\n",
            encoding="utf-8")
        inf = _scan(root)
        if any("UpdateStateMachine.cs" in r and "D005" in r for r in inf):
            print("  ok: D005 flagged pure-logic File. use")
        else:
            print(f"  ✗ D005 not flagged: {inf}")
            failed = 1
        if not any("HarnessRuntimeHost.cs" in r for r in inf):
            print("  ok: D005 whitelisted infra file not flagged")
        else:
            print(f"  ✗ infra file flagged: {inf}")
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
    parser.add_argument("--src", default=DEFAULT_SRC)
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any violation (default: report only)")
    args = parser.parse_args()

    rows = _scan(Path(args.src))
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
