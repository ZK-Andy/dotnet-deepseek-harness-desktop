#!/usr/bin/env python3
"""Verify the README tests/coverage badges agree with the machine test baseline.

Two homes describe the same two facts (test pass count, coverage rate): the
badges in `README.md` / `README.en.md` (the published floor) and the machine
home `scripts/test-baseline.json` (the only parsed source). Keeping them in step
used to rest on the session-close README check alone, and the drift stayed
invisible for months once. This gate asserts the two homes are EQUAL.

The machine home sits outside `docs/**` on purpose: bumping the two values must
stay a LIGHT-tier change, while any `docs/**` edit is a behavior-surface FULL
tier change in `scripts/verify-review-tier.py` (three-way review). Rationale and
alternatives: `.agents/notes/implemented/process/
2026-09-19-baseline-home-tier-decoupling.md`.

Rule source of truth for the equality rule: `.agents/notes/implemented/testing/
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
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path
from urllib.parse import unquote

BASELINE_HOME = "scripts/test-baseline.json"
README_FILES = ("README.md", "README.en.md")

# The machine home's shape contract: exactly these two keys (each stated once),
# each a string in one of these forms. Its whole point is to carry just the two
# facts the badges mirror; provenance (CI run id, covered/valid union, code
# face) lives in the coverage-baseline ADR ledger. Any other shape fails loud
# rather than skipping a comparison.
BASELINE_KEYS = ("tests", "coverage")
TESTS_RE = re.compile(r"\A\d+/\d+\Z")
COVERAGE_RE = re.compile(r"\A\d+(?:\.\d+)?%\Z")

# shields static badge: img.shields.io/badge/<kind>-<urlencoded value>-<color>
BADGE_RE = re.compile(
    r"img\.shields\.io/badge/(?P<kind>tests|coverage)-(?P<payload>[^\"'\s>]+)")


class _DuplicateKeyError(ValueError):
    """A JSON object repeated a key — each fact is stated once in the home."""


def _single_key_object(pairs: list[tuple[str, object]]) -> dict:
    """Build a JSON object, rejecting a repeated key (the anti-ambiguity half of
    the previous "exactly one match" baseline-line contract)."""
    out: dict = {}
    for key, value in pairs:
        if key in out:
            raise _DuplicateKeyError(key)
        out[key] = value
    return out


def _baseline_facts(repo: Path, staged: bool = False
                    ) -> tuple[tuple[str, str] | None, str | None]:
    """((tests "passed/total", coverage "rate%"), problem) from the machine home.

    A missing/unreadable/undecodable file, invalid JSON, a non-object, a
    missing/extra/repeated key, or a value outside its form is a problem (fail
    loud), never a silent skip."""
    text, problem = _read(repo, BASELINE_HOME, staged)
    if problem:
        return None, (f"{BASELINE_HOME}: {problem} "
                      "(the machine baseline home is the only parsed source)")
    try:
        data = json.loads(text or "", object_pairs_hook=_single_key_object)
    except _DuplicateKeyError as exc:
        return None, (f"{BASELINE_HOME}: duplicate key {exc.args[0]!r} "
                      "(each fact is stated once)")
    except json.JSONDecodeError as exc:
        return None, f"{BASELINE_HOME}: invalid JSON ({exc.msg} at line {exc.lineno})"
    if not isinstance(data, dict):
        return None, f"{BASELINE_HOME}: top level must be a JSON object"
    if set(data) != set(BASELINE_KEYS):
        return None, (f"{BASELINE_HOME}: keys must be exactly {list(BASELINE_KEYS)}, "
                      f"got {sorted(data)}")
    tests, coverage = data["tests"], data["coverage"]
    if not isinstance(tests, str) or not TESTS_RE.match(tests):
        return None, f'{BASELINE_HOME}: "tests" must look like "695/695", got {tests!r}'
    if not isinstance(coverage, str) or not COVERAGE_RE.match(coverage):
        return None, f'{BASELINE_HOME}: "coverage" must look like "57.69%", got {coverage!r}'
    return (tests, coverage), None


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
    """Violations, plus the parsed baseline facts (None when the machine home
    itself is the violation) so the caller reports without re-reading it."""
    facts, problem = _baseline_facts(repo, staged)
    if problem:
        return [problem], None
    assert facts is not None, "_baseline_facts returns facts whenever problem is None"
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
                           f"{BASELINE_HOME} baseline = {want!r}")
    return out, facts


def _self_test() -> int:
    """Offline fixtures, each pinning one judgement: agreement passes; a
    coverage drift; a tests drift reported per README; a missing badge; an
    invalid-JSON home; an extra key; a wrong-shaped value; the index-vs-working-
    tree split; a missing baseline home; a missing README; two badges of one
    kind; an undecodable index blob; a non-object home; a repeated key; and the
    home's location rule."""
    failed = 0

    def ok(cond: bool, msg: str) -> None:
        nonlocal failed
        if cond:
            print(f"  ok: {msg}")
        else:
            print(f"  \u2717 {msg}", file=sys.stderr)
            failed = 1

    baseline_json = '{\n  "tests": "569/569",\n  "coverage": "55.22%"\n}\n'
    readme = ('<p align="center">\n'
              '  <a href="x"><img src="https://img.shields.io/badge/tests-569%2F569-brightgreen" alt="tests"></a>\n'
              '  <a href="docs/testing.md"><img src="https://img.shields.io/badge/coverage-55.22%25-yellowgreen" alt="coverage"></a>\n'
              '</p>\n')

    with tempfile.TemporaryDirectory() as td:
        def fixture(name: str, baseline: str, zh: str, en: str | None = None) -> Path:
            repo = Path(td) / name
            (repo / "scripts").mkdir(parents=True)
            (repo / BASELINE_HOME).write_text(baseline, encoding="utf-8")
            (repo / "README.md").write_text(zh, encoding="utf-8")
            (repo / "README.en.md").write_text(en if en is not None else zh, encoding="utf-8")
            return repo

        # 1) agreeing badges (url-encoded, as shipped) pass
        r = fixture("f1", baseline_json, readme)
        rows, _facts = _violations(r)
        ok(rows == [], "agreeing badges pass")

        # 2) coverage badge behind the baseline is reported
        r = fixture("f2", baseline_json, readme.replace("coverage-55.22", "coverage-55.37"))
        rows, _ = _violations(r)
        ok(any("README.md: coverage badge" in x for x in rows),
           "coverage drift is reported")

        # 3) tests badge drift is reported on the mirrored README only
        r = fixture("f3", baseline_json, readme,
                    readme.replace("tests-569%2F569", "tests-568%2F569"))
        rows, _ = _violations(r)
        ok(any(x.startswith("README.en.md: tests badge") for x in rows),
           "tests drift is reported per README file")

        # 4) a missing badge fails loud instead of passing silently
        r = fixture("f4", baseline_json, readme.replace("coverage-55.22%25-yellowgreen", "x"))
        rows, _ = _violations(r)
        ok(any("missing `coverage` badge" in x for x in rows),
           "missing badge fails loud")

        # 5) a home that is not valid JSON fails loud (no silent skip)
        r = fixture("f5", "> 测试基线见 README 徽章。\n", readme)
        rows, _ = _violations(r)
        ok(any("invalid JSON" in x for x in rows),
           "a non-JSON home fails loud")

        # 6) an extra key fails loud: the home carries the two facts only
        r = fixture("f6", baseline_json.replace('"coverage"', '"ci_run": "1",\n  "coverage"'),
                    readme)
        rows, _ = _violations(r)
        ok(any("keys must be exactly" in x for x in rows),
           "an extra key in the home fails loud")

        # 7) a value outside its form fails loud
        r = fixture("f7", baseline_json.replace('"569/569"', '"569 of 569"'), readme)
        rows, _ = _violations(r)
        ok(any('"tests" must look like' in x for x in rows),
           "a wrong-shaped value fails loud")

        # 8) --staged judges the index: a drifted baseline staged while the
        #    working tree was restored to agreement is caught (file mode is not)
        def git(repo: Path, *args: str) -> None:
            subprocess.run(["git", *args], cwd=repo, capture_output=True, check=False)

        r = fixture("f8", baseline_json, readme)
        git(r, "init", "-q")
        git(r, "config", "user.email", "t@t")
        git(r, "config", "user.name", "t")
        git(r, "add", "-A")
        git(r, "commit", "-qm", "base")
        (r / BASELINE_HOME).write_text(baseline_json.replace("55.22", "55.00"),
                                       encoding="utf-8")
        git(r, "add", BASELINE_HOME)
        (r / BASELINE_HOME).write_text(baseline_json, encoding="utf-8")
        rows_tree, _ = _violations(r)
        rows_index, _ = _violations(r, staged=True)
        ok(rows_tree == [], "a restored working tree reads as consistent")
        ok(any("coverage badge" in x for x in rows_index),
           "--staged catches the drifted baseline staged in the index")

        # 9) the fail-loud branches that do not depend on badge values
        r = fixture("f9", baseline_json, readme)
        (r / BASELINE_HOME).unlink()
        rows, facts = _violations(r)
        ok(any(f"{BASELINE_HOME}: missing" in x for x in rows) and facts is None,
           "a missing baseline home is a violation without facts")

        r = fixture("f10", baseline_json, readme)
        (r / "README.en.md").unlink()
        rows, _ = _violations(r)
        ok(any(x.startswith("README.en.md: missing") for x in rows),
           "a missing README is a violation")

        r = fixture("f11", baseline_json, readme.replace(
            '<a href="x">', '<a href="x"><img src="https://img.shields.io/badge/tests-569%2F569-brightgreen" alt="t2"></a><a href="x">'))
        rows, _ = _violations(r)
        ok(any("ambiguous" in x for x in rows),
           "two badges of one kind are a violation")

        # 12) a non-UTF-8 baseline staged in the index is a violation, not a
        #     traceback (--staged is pre-commit's mode)
        r = fixture("f12", baseline_json, readme)
        git(r, "init", "-q")
        git(r, "config", "user.email", "t@t")
        git(r, "config", "user.name", "t")
        git(r, "add", "-A")
        git(r, "commit", "-qm", "base")
        (r / BASELINE_HOME).write_bytes(b"{\xff\xfe\x00 broken bytes}\n")
        git(r, "add", BASELINE_HOME)
        rows, facts = _violations(r, staged=True)
        ok(any("not valid UTF-8" in x for x in rows) and facts is None,
           "an undecodable index blob is a violation in --staged mode")

        # 13) a valid-JSON non-object fails loud instead of unpacking nothing
        r = fixture("f13", "[]\n", readme)
        rows, facts = _violations(r)
        ok(any("top level must be a JSON object" in x for x in rows) and facts is None,
           "a non-object home fails loud")

        # 14) a repeated key fails loud: json.loads would silently keep the last
        #     one, which is the ambiguity the old "exactly one match" contract
        #     closed
        r = fixture("f14", baseline_json.replace(
            '"tests": "569/569",', '"tests": "569/569",\n  "tests": "1/1",'), readme)
        rows, _ = _violations(r)
        ok(any("duplicate key" in x for x in rows),
           "a repeated key in the home fails loud")

        # 15) the home's location is a rule, not a convenience: under docs/** every
        #     baseline bump would be a behavior-surface FULL tier change
        #     (scripts/verify-review-tier.py), the cost this home exists to avoid.
        #     The tier gate's fixture pins the bump path set; this pins the home
        #     itself, so moving it back goes red in the holder.
        ok("docs" not in Path(BASELINE_HOME).parts,
           "the machine baseline home stays outside docs/**")

    if failed == 0:
        print("== verify-readme-badges self-test passed ==")
    else:
        print("== verify-readme-badges self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(
        description="Verify README badges agree with scripts/test-baseline.json")
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
