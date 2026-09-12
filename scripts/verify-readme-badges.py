#!/usr/bin/env python3
"""Verify the README tests/coverage badges agree with the docs/testing.md baseline.

Two homes describe the same two facts (test pass count, coverage rate): the
badges in `README.md` / `README.en.md` (the published floor) and the baseline
line in `docs/testing.md`. Keeping them in step used to rest on the
session-close README check alone, and the drift stayed invisible for months
once. This gate asserts the two homes are EQUAL.

Rule source of truth: `.agents/notes/implemented/testing/
2026-09-13-readme-badge-baseline-gate.md`.

Scope: the two facts above only. `release` / `downloads` / `stars` / `platform`
/ `.NET` badges are derived from external services or static constants.

Boundary: equality is not provenance. This gate does not verify the value
against a CI run (`verify-*.py` never runs `dotnet test`); taking the value from
CI cobertura stays a session-close obligation (see the coverage-baseline ADR).

Usage:
    python3 scripts/verify-readme-badges.py [--repo ROOT]
    python3 scripts/verify-readme-badges.py --staged   # pre-commit: the index
    python3 scripts/verify-readme-badges.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import subprocess
import sys
import tempfile
from pathlib import Path
from urllib.parse import unquote

BASELINE_DOC = "docs/testing.md"
README_FILES = ("README.md", "README.en.md")

# The baseline line: `...（当前 569/569，覆盖率 55.22%）...` — the single parsed
# source. Any other shape fails loud rather than skipping a comparison.
BASELINE_RE = re.compile(
    r"当前\s*(?P<passed>\d+)\s*/\s*(?P<total>\d+)\s*[，,]\s*"
    r"覆盖率\s*(?P<rate>\d+(?:\.\d+)?)\s*%")

# shields static badge: img.shields.io/badge/<kind>-<urlencoded value>-<color>
BADGE_RE = re.compile(
    r"img\.shields\.io/badge/(?P<kind>tests|coverage)-(?P<payload>[^\"'\s>]+)")


def baseline_facts(text: str) -> tuple[str, str] | None:
    """Return (tests "passed/total", coverage "rate%") from the baseline line,
    or None when the line is missing or ambiguous (shape contract broken)."""
    hits = BASELINE_RE.findall(text)
    if len(hits) != 1:
        return None
    passed, total, rate = hits[0]
    return f"{passed}/{total}", f"{rate}%"


def badge_facts(text: str) -> dict[str, list[str]]:
    """Decoded values per badge kind found in a README ("tests"/"coverage")."""
    found: dict[str, list[str]] = {}
    for m in BADGE_RE.finditer(text):
        payload = m.group("payload").split("?")[0].split("#")[0]
        # drop the trailing color segment (shields requires one)
        value = unquote(payload.rsplit("-", 1)[0])
        found.setdefault(m.group("kind"), []).append(value)
    return found


def _read(repo: Path, rel: str, staged: bool = False) -> tuple[str | None, str | None]:
    """(content, problem). The content of `rel` as it will land — the index
    version with --staged (the tree being committed), else the working tree —
    and a short reason when it is unusable ("missing" when absent there, "not
    valid UTF-8" when the bytes do not decode), else None. Decoding is done here
    rather than by subprocess so no mode can escape as a traceback."""
    if staged:
        r = subprocess.run(["git", "show", f":{rel}"], cwd=repo,
                           capture_output=True, check=False)
        if r.returncode != 0:
            return None, "missing"
        data = r.stdout
    else:
        try:
            data = (repo / rel).read_bytes()
        except OSError:
            return None, "missing"  # absent or a directory: nothing to parse
    try:
        return data.decode("utf-8"), None
    except UnicodeDecodeError:
        return None, "not valid UTF-8"


def _violations(repo: Path, staged: bool = False) -> tuple[list[str], tuple[str, str] | None]:
    """Violations, plus the parsed baseline facts (None when the baseline line
    itself is the violation) so the caller reports without re-reading it."""
    baseline_text, problem = _read(repo, BASELINE_DOC, staged)
    if problem:
        return [f"{BASELINE_DOC}: {problem} (the baseline line is the only parsed source)"], None
    facts = baseline_facts(baseline_text or "")
    if facts is None:
        return [f"{BASELINE_DOC}: baseline line not found or ambiguous — expected exactly one "
                "`当前 <passed>/<total>，覆盖率 <rate>%`; a shape change must update "
                "scripts/verify-readme-badges.py"], None
    tests, coverage = facts
    expected = {"tests": tests, "coverage": coverage}

    out: list[str] = []
    for rel in README_FILES:
        text, problem = _read(repo, rel, staged)
        if problem:
            out.append(f"{rel}: {problem}")
            continue
        badges = badge_facts(text or "")
        for kind, want in expected.items():
            values = badges.get(kind, [])
            if not values:
                out.append(f"{rel}: missing `{kind}` badge "
                           f"(img.shields.io/badge/{kind}-...)")
            elif len(values) > 1:
                out.append(f"{rel}: {len(values)} `{kind}` badges, ambiguous: {values}")
            elif values[0] != want:
                out.append(f"{rel}: {kind} badge = {values[0]!r}, "
                           f"{BASELINE_DOC} baseline = {want!r}")
    return out, facts


def _self_test() -> int:
    """Offline fixtures, each pinning one judgement: agreement passes; a
    coverage drift; a tests drift reported per README; a missing badge; a
    baseline shape break; the index-vs-working-tree split; a missing baseline
    doc; a missing README; two badges of one kind; an undecodable index blob."""
    failed = 0

    def ok(cond: bool, msg: str) -> None:
        nonlocal failed
        if cond:
            print(f"  ok: {msg}")
        else:
            print(f"  \u2717 {msg}", file=sys.stderr)
            failed = 1

    baseline_line = "> 测试基线以 README 双语徽章为准（当前 569/569，覆盖率 55.22%）；其余文字。\n"
    readme = ('<p align="center">\n'
              '  <a href="x"><img src="https://img.shields.io/badge/tests-569%2F569-brightgreen" alt="tests"></a>\n'
              '  <a href="docs/testing.md"><img src="https://img.shields.io/badge/coverage-55.22%25-yellowgreen" alt="coverage"></a>\n'
              '</p>\n')

    with tempfile.TemporaryDirectory() as td:
        def fixture(name: str, baseline: str, zh: str, en: str | None = None) -> Path:
            repo = Path(td) / name
            (repo / "docs").mkdir(parents=True)
            (repo / BASELINE_DOC).write_text(baseline, encoding="utf-8")
            (repo / "README.md").write_text(zh, encoding="utf-8")
            (repo / "README.en.md").write_text(en if en is not None else zh, encoding="utf-8")
            return repo

        # 1) agreeing badges (url-encoded, as shipped) pass
        r = fixture("f1", baseline_line, readme)
        rows, _facts = _violations(r)
        ok(rows == [], "agreeing badges pass")

        # 2) coverage badge behind the baseline is reported
        r = fixture("f2", baseline_line, readme.replace("coverage-55.22", "coverage-55.37"))
        rows, _ = _violations(r)
        ok(any("README.md: coverage badge" in x for x in rows),
           "coverage drift is reported")

        # 3) tests badge drift is reported on the mirrored README only
        r = fixture("f3", baseline_line, readme,
                    readme.replace("tests-569%2F569", "tests-568%2F569"))
        rows, _ = _violations(r)
        ok(any(x.startswith("README.en.md: tests badge") for x in rows),
           "tests drift is reported per README file")

        # 4) a missing badge fails loud instead of passing silently
        r = fixture("f4", baseline_line, readme.replace("coverage-55.22%25-yellowgreen", "x"))
        rows, _ = _violations(r)
        ok(any("missing `coverage` badge" in x for x in rows),
           "missing badge fails loud")

        # 5) baseline shape change fails loud (no silent skip)
        r = fixture("f5", "> 测试基线见 README 徽章。\n", readme)
        rows, _ = _violations(r)
        ok(any("baseline line not found or ambiguous" in x for x in rows),
           "baseline shape change fails loud")

        # 6) --staged judges the index: a drifted baseline staged while the
        #    working tree was restored to agreement is caught (file mode is not)
        def git(repo: Path, *args: str) -> None:
            subprocess.run(["git", *args], cwd=repo, capture_output=True, check=False)

        r = fixture("f6", baseline_line, readme)
        git(r, "init", "-q")
        git(r, "config", "user.email", "t@t")
        git(r, "config", "user.name", "t")
        git(r, "add", "-A")
        git(r, "commit", "-qm", "base")
        (r / BASELINE_DOC).write_text(baseline_line.replace("55.22", "55.00"), encoding="utf-8")
        git(r, "add", BASELINE_DOC)
        (r / BASELINE_DOC).write_text(baseline_line, encoding="utf-8")
        rows_tree, _ = _violations(r)
        rows_index, _ = _violations(r, staged=True)
        ok(rows_tree == [], "a restored working tree reads as consistent")
        ok(any("coverage badge" in x for x in rows_index),
           "--staged catches the drifted baseline staged in the index")

        # 7) the three fail-loud branches that do not depend on badge values
        r = fixture("f7", baseline_line, readme)
        (r / BASELINE_DOC).unlink()
        rows, facts = _violations(r)
        ok(any(f"{BASELINE_DOC}: missing" in x for x in rows) and facts is None,
           "a missing baseline doc is a violation without facts")

        r = fixture("f8", baseline_line, readme)
        (r / "README.en.md").unlink()
        rows, _ = _violations(r)
        ok(any(x.startswith("README.en.md: missing") for x in rows),
           "a missing README is a violation")

        r = fixture("f9", baseline_line, readme.replace(
            '<a href="x">', '<a href="x"><img src="https://img.shields.io/badge/tests-569%2F569-brightgreen" alt="t2"></a><a href="x">'))
        rows, _ = _violations(r)
        ok(any("ambiguous" in x for x in rows),
           "two badges of one kind are a violation")

        # 10) a non-UTF-8 baseline staged in the index is a violation, not a
        #     traceback (--staged is pre-commit's mode)
        r = fixture("f10", baseline_line, readme)
        git(r, "init", "-q")
        git(r, "config", "user.email", "t@t")
        git(r, "config", "user.name", "t")
        git(r, "add", "-A")
        git(r, "commit", "-qm", "base")
        (r / BASELINE_DOC).write_bytes(b"> \xff\xfe\x00 broken bytes\n")
        git(r, "add", BASELINE_DOC)
        rows, facts = _violations(r, staged=True)
        ok(any("not valid UTF-8" in x for x in rows) and facts is None,
           "an undecodable index blob is a violation in --staged mode")

    if failed == 0:
        print("== verify-readme-badges self-test passed ==")
    else:
        print("== verify-readme-badges self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(
        description="Verify README badges agree with the docs/testing.md baseline")
    parser.add_argument("--repo", default=".", help="repo root (default cwd)")
    parser.add_argument("--staged", action="store_true",
                        help="read the index (the tree being committed) instead of the "
                             "working tree — pre-commit use")
    args = parser.parse_args()

    repo = Path(args.repo).resolve()
    rows, facts = _violations(repo, staged=args.staged)
    if rows:
        print(f"readme-badges: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        return 1

    assert facts is not None, "_violations returns facts whenever it returns no rows"
    tests, coverage = facts
    print(f"readme-badges: OK (tests {tests}, coverage {coverage})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
