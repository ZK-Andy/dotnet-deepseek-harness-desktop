#!/usr/bin/env python3
"""Verify word budgets for standing docs, driven by a manifest JSON.

Manifest format (doc-budgets.manifest.json at repo root or passed via --manifest):
{
  "budgets": [
    {"path": "AGENTS.md", "max_words": 800},
    {"path": "docs/architecture.md", "max_words": 1200}
  ]
}

Missing file = violation (a budget entry without its file means the doc vanished
or the manifest is stale). Over-limit = violation with word count. Word counts
skip fenced code blocks and table rows — prose is what the budget governs.

Usage:
  python3 verify-doc-budgets.py                 # manifest at ./doc-budgets.manifest.json
  python3 verify-doc-budgets.py --manifest <p>  # explicit manifest path
  python3 verify-doc-budgets.py --self-test     # offline fixtures
Exit code 0 = pass (or self-test passed), 1 = violations (or self-test failed).
"""

import argparse
import json
import re
import sys
import tempfile
from pathlib import Path

WORD_RE = re.compile(r"[\w\u4e00-\u9fff]+", re.UNICODE)


def count_words(text: str) -> int:
    body = re.sub(r"```.*?```", "", text, flags=re.DOTALL)  # skip code blocks
    body = re.sub(r"^\s*\|.*\|\s*$", "", body, flags=re.MULTILINE)  # skip tables
    return len(WORD_RE.findall(body))


def check(manifest: Path) -> tuple[list[str], list[str]] | None:
    """Validate one manifest against its docs.

    Returns (errors, ok_lines); None = manifest missing (the caller prints
    SKIP and passes — a checkout without the local manifest is not a gate hit).
    """
    if not manifest.is_file():
        return None
    data = json.loads(manifest.read_text(encoding="utf-8"))

    errors: list[str] = []
    ok_lines: list[str] = []
    for entry in data["budgets"]:
        doc = Path(entry["path"])
        limit = int(entry["max_words"])
        if not doc.is_file():
            errors.append(f"{doc}: budget entry but file missing (stale manifest?)")
            continue
        words = count_words(doc.read_text(encoding="utf-8"))
        if words > limit:
            errors.append(f"{doc}: {words} words > budget {limit} "
                          f"(relocate, condense, or raise ceiling with justification)")
        else:
            ok_lines.append(f"OK   {doc}: {words}/{limit}")
    return errors, ok_lines


def _self_test() -> int:
    from gate_common import SelfTest

    st = SelfTest()

    # word counting: code blocks and tables excluded, a CJK run is one word
    st.ok(count_words("hello world") == 2, "latin words counted")
    st.ok(count_words("中文测试") == 1, "a CJK run counts as one word")
    st.ok(count_words("中 en 中") == 3, "separated runs count separately")
    st.ok(count_words("keep\n```\nhidden words here\n```\nme") == 2,
          "fenced code block excluded")
    st.ok(count_words("keep\n| a | b table |\nme") == 2,
          "table row excluded")

    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        (root / "small.md").write_text("ten words " * 2, encoding="utf-8")
        (root / "big.md").write_text("word " * 10, encoding="utf-8")
        manifest = root / "m.json"
        # budget paths resolve against the process CWD (existing contract:
        # gates run from the repo root), so the fixture pins absolute paths
        manifest.write_text(json.dumps({"budgets": [
            {"path": str(root / "small.md"), "max_words": 5},
            {"path": str(root / "big.md"), "max_words": 5},
            {"path": str(root / "vanished.md"), "max_words": 10},
        ]}), encoding="utf-8")

        result = check(manifest)
        assert result is not None
        errors, ok_lines = result
        st.ok(ok_lines == [f"OK   {root / 'small.md'}: 4/5"],
              f"in-budget doc reported: {ok_lines}")
        st.ok(errors == [
            f"{root / 'big.md'}: 10 words > budget 5 (relocate, condense, "
            f"or raise ceiling with justification)",
            f"{root / 'vanished.md'}: budget entry but file missing (stale manifest?)",
        ], f"over-budget + stale entry flagged: {errors}")

        st.ok(check(root / "absent.json") is None,
              "missing manifest -> None (caller prints SKIP, not a violation)")

    return st.finish("verify-doc-budgets")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", default="doc-budgets.manifest.json")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return _self_test()

    manifest = Path(args.manifest)
    result = check(manifest)
    if result is None:
        print(f"SKIP: manifest {manifest} not found")
        return 0
    errors, ok_lines = result
    for line in ok_lines:
        print(line)

    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
