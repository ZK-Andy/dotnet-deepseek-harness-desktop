#!/usr/bin/env python3
"""Verify PR increment discipline (coupling shift-left gate ②, report-first).

Large diffs are where coupling hides: a 500-line PR is unreviewable no
matter how clean each hunk looks, and new references to Infrastructure
types are fan-out growth even when each lands in a sanctioned file
(placement is R1's job — volume is this gate's). The n=100 calibration
(2026-09-28..2026-10-12, ADR coupling-increment-baseline) found:
gross p90=545/max=1812, single-file-net p90=170/max=360,
fan-out(code-only short-name refs) nonzero in 27/100 with max=76,
new package refs in 1/100. Caps below sit near p90: sensitive enough
to flag, loose enough that every historical OVER passed FULL review
anyway — so the gate ships report-first and `--enforce` stays unwired
until one batch of live report data firms the lines (see ADR).

Metrics over one diff moment (`--staged` = index vs HEAD for pre-commit,
`--since BASE` = BASE..HEAD for pre-push/CI, default = worktree vs HEAD):

  gross      added + deleted lines over the diff (numstat).
  file-max   largest single-file net gain (added - deleted).
  fanout     added code references to Infrastructure-declared types
             (short names via the live type inventory, comments/strings/
             usings stripped by `gate_common.CSharpLineScanner`; one line
             can yield several refs).
  packages   added `PackageReference`/`PackageVersion` lines (.csproj/props,
             XML comments not stripped — a commented-out reference still
             counts; acceptable at report level).

Default is report-only (exit 0). `--enforce` exits 1 on any OVER.
`--self-test` runs offline fixtures (no git needed).
A git failure is fail-loud (exit 1): an undeterminable diff is an error,
never an empty change set.

Usage: python3 scripts/verify-increment-discipline.py [--staged|--since BASE] [--enforce]
        python3 scripts/verify-increment-discipline.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gate_common import (CSharpLineScanner, NEW_EXPR_RE, STATIC_ACCESS_RE,
                          TYPE_DECL_RE)

# Draft caps from the n=100 calibration (see module docstring): near-p90
# report markers. --enforce compares against the same lines; wiring it is
# a deliberate later change (FULL面) once live report data lands.
DEFAULT_MAX_GROSS = 600
DEFAULT_MAX_FILE_NET = 150
DEFAULT_MAX_FANOUT = 15
DEFAULT_MAX_PACKAGES = 1
DEFAULT_INFRA_SRC = "src/DeepSeek.Harness.Desktop.Infrastructure"

_USING_RE = re.compile(r"^\s*(?:global\s+)?using\s+")
_PKG_RE = re.compile(r"PackageReference|PackageVersion")


def _git(args: list[str]) -> str:
    proc = subprocess.run(["git", *args], capture_output=True, text=True)
    if proc.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {proc.stderr.strip()}")
    return proc.stdout


def _diff_args(mode: str, since: str) -> list[str]:
    if mode == "staged":
        return ["diff", "--cached"]
    if mode == "since":
        return ["diff", f"{since}..HEAD"]
    return ["diff", "HEAD"]


def _metrics(numstat: str) -> tuple[int, int, str, int]:
    """Return (gross, net, top_path, file_max) from `git diff --numstat` text."""
    gross = 0
    net = 0
    top_path = ""
    file_max = 0
    for line in numstat.splitlines():
        parts = line.split("\t")
        if len(parts) != 3:
            continue
        try:
            added, deleted = int(parts[0]), int(parts[1])
        except ValueError:
            continue  # binary files report `- -`
        gross += added + deleted
        net += added - deleted
        if added - deleted > file_max:
            file_max = added - deleted
            top_path = parts[2]
    return gross, net, top_path, file_max


def _infra_inventory(infra_src: Path, extra_lines: list[str]) -> set[str]:
    """Live type inventory plus declarations added inside the diff itself."""
    names: set[str] = set()
    if infra_src.is_dir():
        for path in infra_src.rglob("*.cs"):
            if "obj" in path.parts or "bin" in path.parts:
                continue
            try:
                raws = path.read_text(encoding="utf-8").splitlines()
            except OSError:
                continue
            for code in CSharpLineScanner(raws).iter_cleaned():
                m = TYPE_DECL_RE.match(code)
                if m:
                    names.add(m.group(1))
    for code in CSharpLineScanner(extra_lines).iter_cleaned():
        m = TYPE_DECL_RE.match(code)
        if m:
            names.add(m.group(1))
    return names


def _is_new_file_header(line: str) -> bool:
    """True for unified-diff `+++` file headers only.

    An added content line can itself start with `++` (e.g. `++i;`
    shows as `+++i;`) — only `+++` alone or `+++ ` + path is a header.
    """
    return line == "+++" or line.startswith("+++ ")


def _plus_sections(diff_text: str) -> list[tuple[str, list[str]]]:
    """Split a unified diff into (path, added-lines) sections."""
    sections: list[tuple[str, list[str]]] = []
    path = ""
    buf: list[str] = []
    for line in diff_text.splitlines():
        if line.startswith("+++ b/"):
            if path or buf:
                sections.append((path, buf))
            path, buf = line[len("+++ b/"):], []
        elif line.startswith("+") and not _is_new_file_header(line):
            buf.append(line[1:])
    if path or buf:
        sections.append((path, buf))
    return sections


def _fanout(sections: list[tuple[str, list[str]]], inventory: set[str]) -> tuple[int, list[str]]:
    """Count added code references to infra-declared short names.

    Same-layer references (a file inside the Infrastructure tree calling
    its own layer) are normal growth, not fan-out — only cross-layer
    references (Presentation/tests/tools) count.
    """
    hits: list[str] = []
    for path, lines in sections:
        if path.startswith("src/DeepSeek.Harness.Desktop.Infrastructure/"):
            continue
        for code in CSharpLineScanner(lines).iter_cleaned():
            if not code.strip() or _USING_RE.match(code):
                continue
            for m in NEW_EXPR_RE.finditer(code):
                if m.group(1).split(".")[-1] in inventory:
                    hits.append(code.strip()[:100])
            for m in STATIC_ACCESS_RE.finditer(code):
                if m.group(1) in inventory:
                    hits.append(code.strip()[:100])
    return len(hits), hits[:5]


def _packages(plus_lines: list[str]) -> int:
    return sum(1 for line in plus_lines if _PKG_RE.search(line))


def _plus_lines(diff_text: str) -> list[str]:
    return [line[1:] for line in diff_text.splitlines()
            if line.startswith("+") and not _is_new_file_header(line)]


def _measure(mode: str, since: str) -> dict:
    args = _diff_args(mode, since)
    numstat = _git([*args, "--numstat"])
    gross, net, top_path, file_max = _metrics(numstat)
    cs_diff = _git([*args, "-U0", "--", "*.cs"])
    cs_sections = _plus_sections(cs_diff)
    cs_plus = [line for _, lines in cs_sections for line in lines]
    proj_diff = _git([*args, "-U0", "--", "*.csproj", "*.props"])
    proj_plus = _plus_lines(proj_diff)
    inventory = _infra_inventory(Path(DEFAULT_INFRA_SRC), cs_plus)
    fanout, examples = _fanout(cs_sections, inventory)
    packages = _packages(proj_plus)
    return {"gross": gross, "net": net, "top_path": top_path,
            "file_max": file_max, "fanout": fanout,
            "fanout_examples": examples, "packages": packages}


def _check(m: dict, caps: dict) -> list[str]:
    rows = []
    if m["gross"] > caps["gross"]:
        rows.append(f"  gross {m['gross']} > {caps['gross']} (split the PR or justify in the ADR)")
    if m["file_max"] > caps["file"]:
        rows.append(f"  single-file net +{m['file_max']} > +{caps['file']} ({m['top_path'] or '?'})")
    if m["fanout"] > caps["fanout"]:
        rows.append(f"  new infra refs {m['fanout']} > {caps['fanout']}")
        rows.extend(f"    e.g. {e}" for e in m["fanout_examples"])
    if m["packages"] > caps["packages"]:
        rows.append(f"  new package refs {m['packages']} > {caps['packages']} (new dependency needs review)")
    return rows


def _self_test() -> int:
    failed = 0

    gross, _, top, fmax = _metrics("10\t2\ta.cs\n0\t30\tb.cs\n-\t-\tbin.dat\n")
    if (gross, top, fmax) == (42, "a.cs", 8):
        print("  ok: numstat gross/file-max (binary skipped)")
    else:
        print(f"  ✗ numstat: {(gross, top, fmax)}")
        failed = 1

    inv = {"Engine"}
    n, _ = _fanout([("Shell/Bad.cs", ["var e = new Engine();",
                                      "Engine.Shared();",
                                      "// new Engine();",
                                      'Log("new Engine();");',
                                      "using Infra;",
                                      "var x = new Other();"]),
                    ("src/DeepSeek.Harness.Desktop.Infrastructure/Inside.cs",
                     ["Engine.Shared();"])], inv)
    if n == 2:
        print("  ok: fanout counts cross-layer code only (comment/string/using/same-layer skipped)")
    else:
        print(f"  ✗ fanout counted {n}, want 2")
        failed = 1

    # One line can yield several refs; `+++i;`-shaped content survives.
    n, _ = _fanout([("Shell/Dense.cs", ["var e = new Engine(Engine.Default());",
                                        "++i;"])], {"Engine", "i"})
    sections = _plus_sections("diff --git a/f.cs b/f.cs\n+++ b/f.cs\n+++i;\n")
    if n == 2 and sections == [("f.cs", ["++i;"])]:
        print("  ok: multi-ref lines count per ref; `+++`-led content kept")
    else:
        print(f"  ✗ dense/header edge: n={n} sections={sections}")
        failed = 1

    inv2 = _infra_inventory(Path("nonexistent"), ["public sealed class Fresh { }"])
    if "Fresh" in inv2:
        print("  ok: inventory picks up declarations added in-diff")
    else:
        print("  ✗ in-diff declarations missed")
        failed = 1

    m = {"gross": 700, "net": 100, "top_path": "a.cs",
         "file_max": 200, "fanout": 20, "fanout_examples": ["e"],
         "packages": 2}
    rows = _check(m, {"gross": 600, "file": 150, "fanout": 15, "packages": 1})
    if len(rows) == 5 and any("gross 700" in r for r in rows):
        print("  ok: all four caps flag")
    else:
        print(f"  ✗ caps: {rows}")
        failed = 1

    if failed == 0:
        print("== verify-increment-discipline self-test passed ==")
    else:
        print("== verify-increment-discipline self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(description="Verify PR increment discipline (gate ②, report-first)")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--staged", action="store_true", help="index vs HEAD (pre-commit)")
    group.add_argument("--since", default="", help="BASE..HEAD (pre-push/CI)")
    parser.add_argument("--max-gross", type=int, default=DEFAULT_MAX_GROSS)
    parser.add_argument("--max-file", type=int, default=DEFAULT_MAX_FILE_NET)
    parser.add_argument("--max-fanout", type=int, default=DEFAULT_MAX_FANOUT)
    parser.add_argument("--max-packages", type=int, default=DEFAULT_MAX_PACKAGES)
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any OVER (default: report only; wiring it is a later FULL change)")
    args = parser.parse_args()
    mode = "staged" if args.staged else ("since" if args.since else "worktree")

    try:
        m = _measure(mode, args.since)
    except RuntimeError as ex:
        print(f"increment: undeterminable diff ({ex})")
        return 1

    caps = {"gross": args.max_gross, "file": args.max_file,
            "fanout": args.max_fanout, "packages": args.max_packages}
    print(f"  gross: {m['gross']} (cap {caps['gross']}) | "
          f"file-max: +{m['file_max']} {m['top_path'] or ''}(cap +{caps['file']}) | "
          f"fanout: {m['fanout']} (cap {caps['fanout']}) | packages: {m['packages']} (cap {caps['packages']})")
    rows = _check(m, caps)
    for r in rows:
        print(r if r.startswith("  ") else f"  {r}")
    if rows:
        print(f"increment-discipline: {len([r for r in rows if not r.startswith('    ')])} OVER (report-only)")
        if args.enforce:
            return 1
    else:
        print("increment-discipline: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
