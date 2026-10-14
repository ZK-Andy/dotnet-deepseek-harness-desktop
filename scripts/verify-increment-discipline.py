#!/usr/bin/env python3
"""Verify PR increment discipline (coupling shift-left gate ②, enforcing).

Large diffs are where coupling hides: a 500-line PR is unreviewable no
matter how clean each hunk looks, and new references to Infrastructure
types are fan-out growth even when each lands in a sanctioned file
(placement is R1's job — volume is this gate's). The n=100 calibration
(2026-09-28..2026-10-12, ADR coupling-increment-baseline) found:
gross p90=545/max=1812, single-file-net p90=170/max=360,
fan-out(code-only short-name refs) nonzero in 27/100 with max=76,
new package refs in 1/100. Caps below sit near p90: sensitive enough
to flag, loose enough that every historical OVER passed FULL review
anyway. Report-first ran from 2026-10-12 to the first live batch, whose
only OVER was a brand-new 362-line gate script — a shape with nothing to
split or trim, since a file the diff adds has its whole length as net
gain. So `--enforce` is live (hooks + CI) and file-max reads growth of
files that already existed at BASE only (ADR coupling-increment-enforce).

Metrics over one diff moment (`--staged` = index vs HEAD for pre-commit,
`--since BASE` = BASE..HEAD for pre-push/CI, default = worktree vs HEAD plus
untracked files):

  gross      added + deleted lines over the diff (numstat), new files
             included. The default moment adds every untracked file's line
             count: `git diff` cannot see them, so a fully unstaged batch read
             far below its real size — 438 vs 1831 on the batch that found it
             (ADR increment-gate-default-untracked). The other two moments need
             no such term (the index and the commits already carry the files).
  file-max   largest net gain (added - deleted) among files that existed
             at BASE (ADR coupling-increment-enforce); gross already
             bounds what a new file can contribute. Untracked files are
             additions too, so the default moment keeps them out of it.
  fanout     added code references to Infrastructure-declared types
             (short names via the live type inventory plus declarations the
             diff itself adds under Infrastructure/, comments/strings/usings
             stripped by `gate_common.CSharpLineScanner`; one line can yield
             several refs). Types the diff declares elsewhere (Core/
             Presentation/tests) are not cross-layer targets and stay out of
             the inventory (ADR increment-gate-metric-corrections).
  packages   distinct package ids added (`PackageReference`/`PackageVersion`
             opening tags in .csproj/props; XML comments not stripped — a
             commented-out reference still counts). Distinct ids, not lines:
             central package management makes one dependency cost two lines.
             A matched line without `Include=`/`Update=` (a closing tag is not
             matched) counts as one id-less entry (ADR
             increment-gate-metric-corrections).

`--enforce` (what the hook/CI wiring passes) exits 1 on any OVER; without
it a run is report-only. `--self-test` runs offline fixtures (one of them
builds a throwaway git repo).
A git failure is fail-loud (exit 1): an undeterminable diff is an error,
never an empty change set.

Usage: python3 scripts/verify-increment-discipline.py [--staged|--since BASE] [--enforce]
        python3 scripts/verify-increment-discipline.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gate_common import (CSharpLineScanner, NEW_EXPR_RE, STATIC_ACCESS_RE,
                          TYPE_DECL_RE)

# Caps from the n=100 calibration (2026-09-28..2026-10-12, ADR
# coupling-increment-baseline): near-p90 lines, unchanged when `--enforce`
# went live. Under the existing-only file-max measure that same window gives
# p90=89/max=187, so 150 still sits between the two
# (ADR coupling-increment-enforce).
DEFAULT_MAX_GROSS = 600
DEFAULT_MAX_FILE_NET = 150
DEFAULT_MAX_FANOUT = 15
DEFAULT_MAX_PACKAGES = 1
DEFAULT_INFRA_SRC = "src/DeepSeek.Harness.Desktop.Infrastructure"
# file-max is measured over pre-existing files only: `--diff-filter` takes an
# include-list, so leaving `A` out drops exactly the files this diff adds.
_EXISTING_DIFF_FILTER = "--diff-filter=CDMRTUXB"
# git's `buffer_is_binary` sniffs only the first 8000 bytes; the untracked-file
# line count follows the same window so the two readings agree (ADR
# increment-gate-default-untracked).
_GIT_BINARY_SNIFF_BYTES = 8000

_USING_RE = re.compile(r"^\s*(?:global\s+)?using\s+")
_PKG_RE = re.compile(r"<(?:PackageReference|PackageVersion)\b")


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


def _metrics(numstat: str) -> tuple[int, str, int]:
    """Return (gross, top_path, file_max) from `git diff --numstat` text."""
    gross = 0
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
        if added - deleted > file_max:
            file_max = added - deleted
            top_path = parts[2]
    return gross, top_path, file_max


def _numstat_args(diff_args: list[str], existing_only: bool) -> list[str]:
    """`--numstat` argv for one diff moment; existing_only drops added files."""
    extra = [_EXISTING_DIFF_FILTER] if existing_only else []
    return [*diff_args, "--numstat", *extra]


def _sizes(numstat_all: str, numstat_existing: str) -> tuple[int, str, int]:
    """(gross, top_path, file_max): volume over all files, file-max over existing.

    A file the diff adds has no pre-existing size to grow — its whole length
    is net gain, it cannot be split or trimmed, and gross already bounds it —
    so only files present at BASE feed file-max
    (ADR coupling-increment-enforce).
    """
    gross, _top, _file_max = _metrics(numstat_all)
    _gross, top_path, file_max = _metrics(numstat_existing)
    return gross, top_path, file_max


def _infra_inventory(infra_src: Path,
                     sections: list[tuple[str, list[str]]]) -> set[str]:
    """Live Infrastructure type inventory plus Infrastructure declarations added in-diff.

    Only declarations from Infrastructure files join the inventory: a type the
    diff declares in Core/Presentation/tests is not a cross-layer reference
    target, and counting its own call sites reported every new type plus its
    tests as fan-out (ADR increment-gate-metric-corrections).
    """
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
    prefix = str(infra_src) + "/"
    extra_lines = [line for path, lines in sections
                   if path.startswith(prefix) for line in lines]
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


_PKG_NAME_RE = re.compile(r'(?:Include|Update)\s*=\s*"([^"]+)"')


def _packages(plus_lines: list[str]) -> int:
    """Distinct package ids the diff touches (lines without an id count each).

    Central package management (Directory.Packages.props) makes one new
    dependency cost two lines — `PackageVersion` + `PackageReference` — so a
    line count made the cap of one unreachable for every new dependency
    (ADR increment-gate-metric-corrections).
    """
    names: set[str] = set()
    unnamed = 0
    for line in plus_lines:
        if not _PKG_RE.search(line):
            continue
        m = _PKG_NAME_RE.search(line)
        if m:
            names.add(m.group(1))
        else:
            unnamed += 1
    return len(names) + unnamed


def _plus_lines(diff_text: str) -> list[str]:
    return [line[1:] for line in diff_text.splitlines()
            if line.startswith("+") and not _is_new_file_header(line)]


def _untracked_paths() -> list[tuple[Path, str]]:
    """(path to read, repo-relative key) for files git neither tracks nor ignores.

    `--full-name` yields repo-root-relative paths; the section keys keep that
    form because the fan-out / inventory checks match prefixes on it. The read
    path is joined with the toplevel, so the gate works from any working
    directory.
    """
    root = Path(_git(["rev-parse", "--show-toplevel"]).strip())
    out = _git(["ls-files", "--others", "--exclude-standard", "--full-name"])
    return [(root / line, line) for line in out.splitlines() if line]


def _line_count(data: bytes) -> int | None:
    """Lines a whole-file addition contributes; None when git calls it binary.

    Mirrors `git diff --numstat` on a new file: a trailing partial line still
    counts, and a NUL inside git's sniff window (`_GIT_BINARY_SNIFF_BYTES`,
    `buffer_is_binary`) makes git report `- -` and count nothing — a NUL past
    that window still counts as text.
    """
    if b"\0" in data[:_GIT_BINARY_SNIFF_BYTES]:
        return None
    if not data:
        return 0
    count = data.count(b"\n")
    return count if data.endswith(b"\n") else count + 1


def _untracked_additions() -> tuple[int, list[tuple[str, list[str]]], list[str]]:
    """(gross lines, .cs sections, .csproj/.props lines) for untracked files.

    Only the default moment needs this: `--staged` sees added files through the
    index and `--since BASE` through the commits. Without the term a fully
    unstaged batch reads far below its real size (ADR
    increment-gate-default-untracked). One bytes read per file feeds both the
    line count and (for source/project suffixes) the UTF-8 decode.
    """
    gross = 0
    cs_sections: list[tuple[str, list[str]]] = []
    proj_lines: list[str] = []
    for path, key in _untracked_paths():
        try:
            data = path.read_bytes()
        except OSError:
            continue
        count = _line_count(data)
        if count is None:
            continue
        gross += count
        suffix = Path(key).suffix
        if suffix not in (".cs", ".csproj", ".props"):
            continue
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            continue
        if suffix == ".cs":
            cs_sections.append((key, text.splitlines()))
        else:
            proj_lines.extend(text.splitlines())
    return gross, cs_sections, proj_lines


def _measure(mode: str, since: str) -> dict:
    args = _diff_args(mode, since)
    numstat = _git(_numstat_args(args, existing_only=False))
    numstat_existing = _git(_numstat_args(args, existing_only=True))
    gross, top_path, file_max = _sizes(numstat, numstat_existing)
    cs_diff = _git([*args, "-U0", "--", "*.cs"])
    cs_sections = _plus_sections(cs_diff)
    proj_diff = _git([*args, "-U0", "--", "*.csproj", "*.props"])
    proj_plus = _plus_lines(proj_diff)
    if mode == "worktree":
        untracked_gross, untracked_cs, untracked_proj = _untracked_additions()
        gross += untracked_gross
        cs_sections = [*cs_sections, *untracked_cs]
        proj_plus = [*proj_plus, *untracked_proj]
    inventory = _infra_inventory(Path(DEFAULT_INFRA_SRC), cs_sections)

    fanout, examples = _fanout(cs_sections, inventory)
    packages = _packages(proj_plus)
    return {"gross": gross, "top_path": top_path,
            "file_max": file_max, "fanout": fanout,
            "fanout_examples": examples, "packages": packages}


def _check(m: dict, caps: dict) -> list[str]:
    rows = []
    if m["gross"] > caps["gross"]:
        rows.append(f"  gross {m['gross']} > {caps['gross']} (split the PR or justify in the ADR)")
    if m["file_max"] > caps["file"]:
        rows.append(f"  file-max(existing) +{m['file_max']} > +{caps['file']} ({m['top_path'] or '?'})")
    if m["fanout"] > caps["fanout"]:
        rows.append(f"  new infra refs {m['fanout']} > {caps['fanout']}")
        rows.extend(f"    e.g. {e}" for e in m["fanout_examples"])
    if m["packages"] > caps["packages"]:
        rows.append(f"  new package ids {m['packages']} > {caps['packages']} (new dependency needs review)")
    return rows


def _self_test() -> int:
    failed = 0

    gross, top, fmax = _metrics("10\t2\ta.cs\n0\t30\tb.cs\n-\t-\tbin.dat\n")
    if (gross, top, fmax) == (42, "a.cs", 8):
        print("  ok: numstat gross/file-max (binary skipped)")
    else:
        print(f"  ✗ numstat: {(gross, top, fmax)}")
        failed = 1

    # A file the diff adds is not file growth: only files present at BASE feed
    # file-max, while gross still counts every file (ADR coupling-increment-enforce).
    args_all = _numstat_args(["diff", "--cached"], existing_only=False)
    args_existing = _numstat_args(["diff", "--cached"], existing_only=True)
    if (args_all == ["diff", "--cached", "--numstat"]
            and args_existing == ["diff", "--cached", "--numstat", _EXISTING_DIFF_FILTER]):
        print("  ok: existing-only numstat args carry the filter that drops added files")
    else:
        print(f"  ✗ numstat args: all={args_all} existing={args_existing}")
        failed = 1

    sizes = _sizes("400\t0\tnew.py\n100\t20\texisting.py\n", "100\t20\texisting.py\n")
    if sizes == (520, "existing.py", 80):
        print("  ok: new-file net excluded from file-max, kept in gross")
    else:
        print(f"  ✗ sizes: {sizes}")
        failed = 1

    # An untracked file is a whole-file addition: a trailing partial line still
    # counts, and binary follows git's sniff window (a NUL past the first 8000
    # bytes still counts as text).
    with tempfile.TemporaryDirectory() as td:
        partial = Path(td) / "partial.txt"
        partial.write_text("a\nb", encoding="utf-8")
        binary = Path(td) / "bin.dat"
        binary.write_bytes(b"a\0b\n")
        late_nul = Path(td) / "late.dat"
        late_nul.write_bytes(b"x\n" * 4000 + b"\0" + b"y\n" * 10)
        got = (_line_count(partial.read_bytes()), _line_count(binary.read_bytes()),
               _line_count(late_nul.read_bytes()))
        if got == (2, None, 4010):
            print("  ok: untracked line count (missing trailing newline / binary sniff window)")
        else:
            print(f"  ✗ untracked line count: {got} (want (2, None, 4010))")
            failed = 1

    # End-to-end on a throwaway repo: the real git path decides which files the
    # filter drops, so a dropped or inverted `--diff-filter` (lowercase letters
    # are an EXCLUDE list) cannot pass unnoticed — the argv fixture above only
    # states the intent, it compares the constant to itself.
    with tempfile.TemporaryDirectory() as td:
        repo = Path(td) / "e2e"
        repo.mkdir()

        def _e2e_git(*argv: str) -> None:
            subprocess.run(["git", "-C", str(repo), *argv],
                           check=True, capture_output=True, text=True)

        _e2e_git("init", "-q")
        _e2e_git("config", "user.email", "t@t")
        _e2e_git("config", "user.name", "t")
        (repo / "old.txt").write_text("base\n", encoding="utf-8")
        (repo / "big.txt").write_text("x\n" * 100, encoding="utf-8")
        _e2e_git("add", ".")
        _e2e_git("commit", "-qm", "base")
        (repo / "old.txt").write_text("base\n" + "added\n" * 80, encoding="utf-8")
        (repo / "new.py").write_text("n\n" * 400, encoding="utf-8")
        (repo / "big.txt").write_text("y\n" * 300, encoding="utf-8")
        _e2e_git("mv", "big.txt", "rew.txt")
        _e2e_git("add", "-A")
        cwd = Path.cwd()
        os.chdir(repo)
        try:
            m = _measure("staged", "")
        finally:
            os.chdir(cwd)
        # old.txt +80 is the only pre-existing growth: new.py (added) and
        # rew.txt (added side of a rename split at <50% similarity) are out.
        if (m["gross"], m["top_path"], m["file_max"]) == (880, "old.txt", 80):
            print("  ok: e2e — added files and rename add-side stay out of file-max")
        else:
            print(f"  ✗ e2e: gross={m['gross']} file_max={m['file_max']} top={m['top_path']}")
            failed = 1

        # `git diff` cannot see untracked files: the default (worktree) moment
        # must add them or a fully unstaged batch reads low (ADR
        # increment-gate-default-untracked). Staged/since moments already carry
        # them, so the same tree read as `staged` stays at 880.
        (repo / "untracked_extra.txt").write_text("u\n" * 30, encoding="utf-8")
        os.chdir(repo)
        try:
            worktree = _measure("worktree", "")
            staged = _measure("staged", "")
        finally:
            os.chdir(cwd)
        if (worktree["gross"], worktree["file_max"]) == (910, 80) and staged["gross"] == 880:
            print("  ok: e2e — worktree moment adds untracked files; staged moment does not")
        else:
            print(f"  ✗ e2e untracked: worktree={worktree['gross']}/+{worktree['file_max']}"
                  f" staged={staged['gross']}")
            failed = 1

        # An untracked section keeps its repo-relative key: an untracked
        # Infrastructure file is same-layer growth, not cross-layer fan-out
        # (an absolute key would slip past the same-layer prefix check).
        infra = repo / "src" / "DeepSeek.Harness.Desktop.Infrastructure"
        infra.mkdir(parents=True)
        (infra / "Engine.cs").write_text("public sealed class Engine { }\n", encoding="utf-8")
        _e2e_git("add", "-A")
        _e2e_git("commit", "-qm", "infra base")
        (infra / "Inside.cs").write_text("Engine.Shared();\n", encoding="utf-8")
        os.chdir(repo)
        try:
            same_layer = _measure("worktree", "")
        finally:
            os.chdir(cwd)
        if same_layer["fanout"] == 0:
            print("  ok: e2e — untracked same-layer Infrastructure file is not fan-out")
        else:
            print(f"  ✗ e2e untracked same-layer fanout: {same_layer['fanout']}"
                  f" {same_layer['fanout_examples']}")
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

    inv2 = _infra_inventory(Path("src/App.Infrastructure"), [
        ("src/App.Infrastructure/Platform/Fresh.cs", ["public sealed class Fresh { }"]),
        ("src/App.Core/Thing.cs", ["public sealed class Thing { }"]),
    ])
    if "Fresh" in inv2 and "Thing" not in inv2:
        print("  ok: in-diff declarations join the inventory from Infrastructure only")
    else:
        print(f"  ✗ inventory scope wrong: {sorted(inv2)}")
        failed = 1

    # CPM costs two lines per dependency; the cap counts ids, not lines.
    if _packages(['    <PackageVersion Include="Ryn.Plugins.Dialog" Version="0.38.0" />',
                  '    <PackageReference Include="Ryn.Plugins.Dialog" />']) == 1:
        print("  ok: one dependency counted once across CPM's two lines")
    else:
        print("  ✗ CPM dependency double-counted")
        failed = 1
    if (_packages(['<PackageReference Include="A" />',
                   '<PackageReference Include="B" />']) == 2
            and _packages(['<PackageReference />']) == 1):
        print("  ok: distinct ids counted separately; id-less lines still count")
    else:
        print("  ✗ package id counting wrong")
        failed = 1
    if _packages(['    <PackageReference Include="coverlet.collector">',
                  '      <IncludeAssets>all</IncludeAssets>',
                  '    </PackageReference>']) == 1:
        print("  ok: multi-line element with children counts once (closing tag not matched)")
    else:
        print("  ✗ closing tag double-counted")
        failed = 1

    m = {"gross": 700, "top_path": "a.cs",
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

    parser = argparse.ArgumentParser(description="Verify PR increment discipline (gate ②, enforcing)")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--staged", action="store_true", help="index vs HEAD (pre-commit)")
    group.add_argument("--since", default="", help="BASE..HEAD (pre-push/CI)")
    parser.add_argument("--max-gross", type=int, default=DEFAULT_MAX_GROSS)
    parser.add_argument("--max-file", type=int, default=DEFAULT_MAX_FILE_NET)
    parser.add_argument("--max-fanout", type=int, default=DEFAULT_MAX_FANOUT)
    parser.add_argument("--max-packages", type=int, default=DEFAULT_MAX_PACKAGES)
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any OVER (default: report only)")
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
          f"file-max(existing): +{m['file_max']} {m['top_path'] or ''}(cap +{caps['file']}) | "
          f"fanout: {m['fanout']} (cap {caps['fanout']}) | packages: {m['packages']} (cap {caps['packages']})")
    rows = _check(m, caps)
    for r in rows:
        print(r if r.startswith("  ") else f"  {r}")
    if rows:
        over = len([r for r in rows if not r.startswith("    ")])
        mode = "enforced" if args.enforce else "report-only"
        print(f"increment-discipline: {over} OVER ({mode})")
        if args.enforce:
            return 1
    else:
        print("increment-discipline: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
