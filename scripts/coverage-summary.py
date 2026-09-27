#!/usr/bin/env python3
"""Summarize the merged line coverage of a multi-test-project `dotnet test` run.

`dotnet test <sln>` starts one test host per test project, so
`--collect:"XPlat Code Coverage"` emits ONE `coverage.cobertura.xml` PER test
project. Each file covers the assemblies that project references and reports
every source line of them (hits 0 where that project's tests never reach), so a
single file's `line-rate` describes one project's slice, not the run. The
baseline track (README badges / `scripts/test-baseline.json`) is the UNION over
the files: key
`(assembly, source path, line)`, hits = max across files. Coverlet writes the
path as `<Assembly>/Services/X.cs` in some files and `Services/X.cs` in
others, so a leading path segment equal to the package (assembly) name is
stripped before the key is formed — otherwise the same physical line would be
counted twice and the rate would drift down.

Rule source of truth: `.agents/notes/implemented/testing/
2026-09-14-coverage-baseline-multi-project-merge.md`.

`--baseline` turns the print into a gate: the merged rate is compared against
the `coverage` value of `scripts/test-baseline.json` and the run FAILS when it
drops more than `--tolerance-pp` percentage points below it. The tolerance is
not slack for its own sake: the recorded baseline carries two decimals and a
re-run of the same commit drifts by a few instrumented lines (same-commit
re-runs differ in `HostLog`'s unwritable-HOME `catch`, 2026-09-12), so an exact
comparison would turn CI red at random. Both sides are rounded to the recorded
two decimals before comparing, so two values that the log prints as equal
compare as equal; rounding is monotone, which means it can only ever be more
lenient than the raw comparison, never stricter (the whole effect lives inside
0.01pp, three orders below the tolerance).

The tolerance value and the rationale for having one at all live in
`.agents/notes/implemented/process/2026-09-28-ci-gate-honesty.md` and
`docs/testing.md`.

Usage:
    python3 scripts/coverage-summary.py [--results TestResults]
    python3 scripts/coverage-summary.py --results TestResults \
        --baseline scripts/test-baseline.json --tolerance-pp 0.5
    python3 scripts/coverage-summary.py --self-test
Exit code 0 = files merged and printed (and, with `--baseline`, not below the
tolerance floor); 1 = nothing to report either way — no cobertura files found
(a missing artifact must not print a plausible 0%) or the files carry 0
instrumented lines; 2 = malformed XML; 3 = measured rate below the baseline
floor; 4 = the baseline file is missing or malformed. The two exit-1 causes
print their own stderr line, so the code alone does not separate them.
"""

import argparse
import contextlib
import glob
import io
import json
import re
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

SUMMARY_PREFIX = "coverage-summary:"
DEFAULT_TOLERANCE_PP = 0.5
BASELINE_COVERAGE_RE = re.compile(r"\d+(?:\.\d+)?%")


def _load_baseline_pct(path: str) -> float:
    """Return the baseline coverage as a percentage number (e.g. 59.95).

    The `coverage` value's form is checked with the same regex
    `verify-readme-badges.py` uses, so the two readers agree on what a
    well-formed rate looks like. This reader is the wider of the two on the
    file: it does not repeat that script's exact-key-set and duplicate-key
    rules (a file only this reader would accept is still rejected by
    `verify-readme-badges.py`, which runs in the same CI job and pre-commit).
    Unifying them into one shared helper is the `docs/script-standards.md`
    shared-library item, not this batch. Anything that fails raises ValueError
    so main() can fail loud — an unreadable baseline must never read as "no
    floor, pass".
    """
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(data, dict) or "coverage" not in data:
        raise ValueError("not a JSON object carrying a 'coverage' key")
    text = data["coverage"]
    if not isinstance(text, str) or BASELINE_COVERAGE_RE.fullmatch(text.strip()) is None:
        raise ValueError(f"coverage value {text!r} is not '<rate>%'")
    return float(text.strip()[:-1])


def _normalize(package: str, filename: str) -> str:
    """Strip a leading path segment equal to the assembly (package) name so the
    same source file keys identically across test projects."""
    parts = filename.split("/")
    if len(parts) > 1 and parts[0] == package:
        return "/".join(parts[1:])
    return filename


def _merge(paths: list[str]) -> tuple[int, int, dict[str, list[int]]]:
    """(covered, valid, {package: [covered, valid]}) over the union of the
    cobertura files, taking the max hits per key."""
    lines: dict[tuple[str, str, str], int] = {}
    for path in paths:
        root = ET.parse(path).getroot()
        for pkg in root.iter("package"):
            package = pkg.get("name") or ""
            for cls in pkg.iter("class"):
                filename = _normalize(package, cls.get("filename") or "")
                for ln in cls.iter("line"):
                    number = ln.get("number")
                    if number is None:
                        continue
                    key = (package, filename, number)
                    hits = int(ln.get("hits", "0"))
                    if hits > lines.get(key, -1):
                        lines[key] = hits

    per_package: dict[str, list[int]] = {}
    for (package, _filename, _number), hits in lines.items():
        bucket = per_package.setdefault(package, [0, 0])
        bucket[1] += 1
        if hits > 0:
            bucket[0] += 1
    covered = sum(v[0] for v in per_package.values())
    valid = sum(v[1] for v in per_package.values())
    return covered, valid, per_package


def _report(paths: list[str], baseline_pct: float | None = None,
            tolerance_pp: float = DEFAULT_TOLERANCE_PP) -> int:
    try:
        covered, valid, per_package = _merge(paths)
    except ET.ParseError as exc:
        print(f"{SUMMARY_PREFIX} malformed cobertura XML: {exc}", file=sys.stderr)
        return 2
    if valid == 0:
        print(f"{SUMMARY_PREFIX} 0 instrumented lines across {len(paths)} file(s)",
              file=sys.stderr)
        return 1
    print(f"{SUMMARY_PREFIX} merged {len(paths)} cobertura file(s)")
    for package, (c, t) in sorted(per_package.items()):
        print(f"  {package:<42} {c:>6}/{t:<6} {100 * c / t:6.2f}%")
    rate = covered / valid
    print(f"{SUMMARY_PREFIX} covered={covered} valid={valid} "
          f"line-rate={rate:.4f} ({100 * rate:.2f}%)")
    if baseline_pct is None:
        return 0
    # Both sides rounded to the two decimals the baseline records, so that two
    # values the log prints as equal compare as equal. Rounding is monotone, so
    # it can only be more lenient than comparing raw rates, never stricter; the
    # difference it can make lives inside 0.01pp.
    measured_pct = round(100 * rate, 2)
    floor_pct = round(baseline_pct - tolerance_pp, 2)
    print(f"{SUMMARY_PREFIX} baseline={baseline_pct:.2f}% "
          f"tolerance={tolerance_pp:.2f}pp floor={floor_pct:.2f}% "
          f"measured={measured_pct:.2f}%")
    if measured_pct < floor_pct:
        print(f"{SUMMARY_PREFIX} FAIL: 覆盖率 {measured_pct:.2f}% 低于基线 "
              f"{baseline_pct:.2f}% 超过容差 {tolerance_pp:.2f}pp"
              f"（下限 {floor_pct:.2f}%）——补测试，或按 docs/testing.md「跟值」"
              f"流程同变更更新 scripts/test-baseline.json 与 README 双语徽章",
              file=sys.stderr)
        return 3
    return 0


def _self_test() -> int:
    """Offline fixtures pinning the merge's judgement points (max-hits across
    files, assembly-prefix normalization keeping one physical line on one key)
    and the fail-loud exit branches ci.yml relies on: 1/2 for the merge side,
    3 for a rate under the baseline floor, 4 for an unusable baseline file."""
    failed = 0

    def ok(cond: bool, msg: str) -> None:
        nonlocal failed
        if cond:
            print(f"  ok: {msg}")
        else:
            print(f"  \u2717 {msg}", file=sys.stderr)
            failed = 1

    def cobertura(package: str, filename: str, lines: list[tuple[str, int]]) -> str:
        body = "".join(f'<line number="{n}" hits="{h}"/>' for n, h in lines)
        # Real coverlet classes carry method-level <lines> duplicating the class
        # level ones; emit both so the fixture exercises the double visit.
        return ('<?xml version="1.0" encoding="utf-8"?>\n'
                f'<coverage line-rate="0" version="1.9">'
                f'<packages><package name="{package}"><classes>'
                f'<class name="{package}.C" filename="{filename}">'
                f'<methods><method name="M" signature="()">'
                f'<lines>{body}</lines></method></methods>'
                f'<lines>{body}</lines></class></classes></package></packages>'
                '</coverage>\n')

    with tempfile.TemporaryDirectory() as td:
        # Same physical file, prefixed in one file and bare in the other; the
        # union must hold 4 lines for App (max hits per line), not 6.
        a = Path(td) / "a" / "coverage.cobertura.xml"
        a.parent.mkdir(parents=True)
        a.write_text(cobertura("App", "App/Services/A.cs",
                               [("1", 1), ("2", 0), ("3", 1)]), encoding="utf-8")
        b = Path(td) / "b" / "coverage.cobertura.xml"
        b.parent.mkdir(parents=True)
        b.write_text(cobertura("App", "Services/A.cs",
                               [("1", 0), ("2", 1), ("4", 1)]), encoding="utf-8")
        covered, valid, per_package = _merge([str(a), str(b)])
        ok((covered, valid) == (4, 4), "prefix-normalized lines merge to one key set")
        ok(per_package["App"] == [4, 4], "max hits per line: a 0-hit slice cannot erase a hit")

        c = Path(td) / "c" / "coverage.cobertura.xml"
        c.parent.mkdir(parents=True)
        c.write_text(cobertura("Lib", "Lib/Services/B.cs",
                               [("5", 0), ("6", 1)]), encoding="utf-8")
        covered, valid, per_package = _merge([str(a), str(b), str(c)])
        ok((covered, valid) == (5, 6), "a separate package keeps its own lines")
        ok(sorted(per_package) == ["App", "Lib"], "per-package breakdown is keyed by package")

        # The three fail-loud branches ci.yml depends on: malformed XML -> 2,
        # files with 0 instrumented lines -> 1, no files at all -> 1.
        malformed = Path(td) / "bad" / "coverage.cobertura.xml"
        malformed.parent.mkdir(parents=True)
        malformed.write_text("<coverage", encoding="utf-8")
        empty = Path(td) / "empty" / "coverage.cobertura.xml"
        empty.parent.mkdir(parents=True)
        empty.write_text(cobertura("App", "App/Services/A.cs", []), encoding="utf-8")

        def code_of(paths: list[str]) -> int:
            with contextlib.redirect_stderr(io.StringIO()):
                return _report(paths)

        ok(code_of([str(malformed)]) == 2, "malformed XML exits 2")
        ok(code_of([str(empty)]) == 1, "0 instrumented lines exits 1")

        no_files = Path(td) / "none"
        no_files.mkdir()
        old_argv = sys.argv
        sys.argv = ["coverage-summary.py", "--results", str(no_files)]
        try:
            with contextlib.redirect_stderr(io.StringIO()):
                missing = main()
        finally:
            sys.argv = old_argv
        ok(missing == 1, "no cobertura files exits 1")

        # Baseline gate: fixtures a+b+c merge to 5/6 = 83.33%. 83.83/83.84 are one
        # hundredth of a point apart, pinning where "on the floor" stops passing;
        # 83.834 is the case that pins the rounding itself (raw floor 83.334 would
        # fail, the recorded two-decimal floor 83.33 passes).
        def gate_code(baseline_pct: float, tolerance: float = 0.5) -> int:
            with contextlib.redirect_stdout(io.StringIO()), \
                    contextlib.redirect_stderr(io.StringIO()):
                return _report([str(a), str(b), str(c)], baseline_pct, tolerance)

        ok(gate_code(83.00) == 0, "rate above the baseline passes")
        ok(gate_code(83.80) == 0,
           "rate 0.47pp under the baseline but inside the tolerance passes")
        ok(gate_code(83.83) == 0, "rate sitting exactly on the floor passes")
        ok(gate_code(83.84) == 3, "rate one hundredth under the floor exits 3")
        ok(gate_code(83.834) == 0, "floor is compared at the baseline's own precision")

        def baseline_of(text: str) -> object:
            p = Path(td) / "baseline.json"
            p.write_text(text, encoding="utf-8")
            try:
                return _load_baseline_pct(str(p))
            except (OSError, ValueError, json.JSONDecodeError) as exc:
                return exc

        ok(baseline_of('{"tests": "1/1", "coverage": "59.95%"}') == 59.95,
           "baseline '<rate>%' parses to a percentage number")
        ok(isinstance(baseline_of('{"coverage": "n/a"}'), ValueError),
           "non-percentage coverage value is rejected")
        ok(isinstance(baseline_of('{"coverage": 59.95}'), ValueError),
           "non-string coverage value is rejected")
        ok(isinstance(baseline_of('{"tests": "1/1"}'), ValueError),
           "baseline without a coverage key is rejected")
        ok(isinstance(baseline_of('{"coverage": "59.95%",'), json.JSONDecodeError),
           "malformed JSON baseline is rejected")

        # main() maps an unusable baseline to exit 4 (the gate must never read a
        # broken baseline as "no floor, pass").
        old_argv = sys.argv
        sys.argv = ["coverage-summary.py", "--results", str(Path(td)),
                    "--baseline", str(Path(td) / "absent-baseline.json")]
        try:
            with contextlib.redirect_stdout(io.StringIO()), \
                    contextlib.redirect_stderr(io.StringIO()):
                unusable = main()
        finally:
            sys.argv = old_argv
        ok(unusable == 4, "unusable baseline exits 4")

    if failed == 0:
        print("== coverage-summary self-test passed ==")
    else:
        print("== coverage-summary self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(
        description="Merge per-test-project coverage.cobertura.xml files into one "
                    "baseline line")
    parser.add_argument("--results", default="TestResults",
                        help="directory holding the coverage artifacts (default TestResults)")
    parser.add_argument("--baseline", default=None,
                        help="scripts/test-baseline.json to compare against; "
                             "omitted = print only, no floor")
    parser.add_argument("--tolerance-pp", type=float, default=DEFAULT_TOLERANCE_PP,
                        help="percentage points the rate may sit below the "
                             f"baseline before failing (default {DEFAULT_TOLERANCE_PP})")
    args = parser.parse_args()

    baseline_pct = None
    if args.baseline:
        try:
            baseline_pct = _load_baseline_pct(args.baseline)
        except (OSError, ValueError) as exc:
            print(f"{SUMMARY_PREFIX} unusable baseline {args.baseline}: {exc}",
                  file=sys.stderr)
            return 4

    paths = sorted(glob.glob(str(Path(args.results) / "**" / "coverage.cobertura.xml"),
                             recursive=True))
    if not paths:
        print(f"{SUMMARY_PREFIX} no coverage.cobertura.xml under {args.results}",
              file=sys.stderr)
        return 1
    return _report(paths, baseline_pct, args.tolerance_pp)


if __name__ == "__main__":
    sys.exit(main())
