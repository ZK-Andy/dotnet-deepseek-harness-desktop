#!/usr/bin/env python3
"""Verify the project cookbook (docs/cookbook.md): stage-label closed set,
entry format, and date validity.

The cookbook is the single home for implementation-stage procedural pitfalls
(see root AGENTS.md doc discipline "procedure → cookbook" and the ADR
2026-08-27-adr-naming-and-script-pitfall-records.md). Each entry must carry a
stage label from a closed set so the product/environment/script discriminators
stay machine-checkable — labels degrade into synonyms if left free-form.

Checks:
  1. Every `## <阶段>` heading uses the closed stage set (below).
  2. Every `- **` entry under a stage section:
       - starts with `[<stage>] <主题>（<yyyy-mm-dd> <来源>）**：...`
       - the stage must be one of the closed set
       - must carry a `（yyyy-mm-dd ...）` date, a real calendar day, not after today
       - must have a non-empty topic and non-empty body after the `：`
  3. Unknown / duplicate stage headings, or an entry with a stage that does not
     match its section, are flagged.

Usage: python3 scripts/verify-cookbook.py [cookbook_path]
       python3 scripts/verify-cookbook.py --self-test   # offline fixture self-check
Exit code 0 = pass, 1 = violations.
"""

import datetime
import re
import sys
from pathlib import Path

DEFAULT_PATH = Path("docs/cookbook.md")

# Closed stage set — kept in sync with root AGENTS.md / .agents/AGENTS.md and
# the ADR. Adding or renaming a stage must update all three + this set.
STAGE_SET = ("脚本", "打包", "调试", "环境", "上游", "产品")

# Stage sorted into a canonical display order for the doc; must be a permutation
# of STAGE_SET. Used by --self-test and to keep the section order deterministic.
STAGE_ORDER = ("脚本", "打包", "调试", "环境", "上游", "产品")

# An entry line inside a stage section:
#   - **[脚本] 主题（… yyyy-mm-dd …）**：正文...
#   - **[脚本] 主题（… yyyy-mm-dd …）**，正文...
# The date must appear somewhere in the title area (before the closing **).
# Body may follow with '：', '，', or '；' after the closing **.
ENTRY_RE = re.compile(
    r"^- \*\*\[(?P<stage>[^\]]+)\]\s+(?P<title>.+?)\*\*[：，；](?P<body>.+)$"
)
DATE_RE = re.compile(r"(?P<date>\d{4}-\d{2}-\d{2})")
HEADING_RE = re.compile(r"^## (?P<stage>.+?)\s*$")


def _validate_entry(line: str, path: Path) -> list[str]:
    errors: list[str] = []
    m = ENTRY_RE.match(line)
    if m is None:
        errors.append(f"{path}: entry must match "
                      "'- **[阶段] 主题（… yyyy-mm-dd …）**：正文…'")
        return errors

    stage = m.group("stage")
    if stage not in STAGE_SET:
        errors.append(f"{path}: unknown stage '{stage}' (closed set: {list(STAGE_SET)})")

    title = m.group("title").strip()
    dm = DATE_RE.search(title)
    if dm is None:
        errors.append(f"{path}: entry title must contain a yyyy-mm-dd date")
        return errors
    date_str = dm.group("date")
    try:
        note_date = datetime.date.fromisoformat(date_str)
    except ValueError:
        errors.append(f"{path}: '{date_str}' is not a real calendar date")
        return errors
    if note_date > datetime.date.today():
        errors.append(f"{path}: date '{date_str}' is after today ({datetime.date.today()})")
    if note_date.year < 1970:
        errors.append(f"{path}: date '{date_str}' is before the epoch (1970)")

    if not title:
        errors.append(f"{path}: entry has an empty title")
    body = m.group("body").strip()
    if not body:
        errors.append(f"{path}: entry has an empty body after '：'")
    return errors


def _scan(path: Path) -> tuple[int, list[str]]:
    """Return (entry_count, errors) for a real cookbook."""
    if not path.is_file():
        return 0, [f"{path}: cookbook file not found"]

    errors: list[str] = []
    checked = 0
    current_stage: str | None = None
    seen_stages: list[str] = []
    for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        hm = HEADING_RE.match(line.strip())
        if hm:
            stage = hm.group("stage").strip()
            if stage.startswith("#") or stage == "Gotchas":
                current_stage = None
                continue
            if stage not in STAGE_SET:
                errors.append(f"{path}:{lineno}: unknown stage heading '{stage}' "
                              f"(closed set: {list(STAGE_SET)})")
                current_stage = None
                continue
            if stage in seen_stages:
                errors.append(f"{path}:{lineno}: duplicate stage heading '{stage}'")
            seen_stages.append(stage)
            current_stage = stage
            continue

        if line.strip().startswith("- **"):
            checked += 1
            errors.extend(_validate_entry(line.strip(), f"{path}:{lineno}"))
            m = ENTRY_RE.match(line.strip())
            if m and current_stage is not None and m.group("stage") != current_stage:
                errors.append(f"{path}:{lineno}: entry stage '{m.group('stage')}' "
                              f"does not match section '{current_stage}'")
    return checked, errors


def _self_test() -> int:
    """Offline fixture self-check: build a conforming and a violating cookbook,
    assert the validator flags exactly the expected ones."""
    import tempfile

    HEADERS = "\n".join(f"## {s}" for s in STAGE_ORDER)
    # Use a past date so "not after today" holds regardless of run day.
    PAST = "2026-08-27"

    ok_entry = (
        f"- **[脚本] 示例主题（{PAST} 实机）**：示例正文内容。\n"
    )
    # ok body keeps the entry under its matching stage section.
    ok_body = f"# Cookbook\n\n## 脚本\n\n{ok_entry}\n"

    cases = []  # (content, expected_exit, description)
    cases.append((ok_body, 0, "conforming cookbook -> pass"))

    # bad stage in an entry
    bad_entry = (
        f"- **[未知] 示例主题（{PAST} 实机）**：正文。\n"
    )
    bad_body = f"# Cookbook\n\n{HEADERS}\n\n{bad_entry}\n"
    cases.append((bad_body, 1, "unknown entry stage -> fail"))

    # future date
    future = (datetime.date.today() + datetime.timedelta(days=1)).isoformat()
    future_body = f"# Cookbook\n\n## 脚本\n\n- **[脚本] 示例主题（{future} 实机）**：正文。\n"
    cases.append((future_body, 1, "future date -> fail"))

    # invalid calendar day
    bad_date_body = f"# Cookbook\n\n## 脚本\n\n- **[脚本] 示例主题（2026-02-31 实机）**：正文。\n"
    cases.append((bad_date_body, 1, "invalid calendar date -> fail"))

    # entry stage mismatches section
    mismatch_body = f"# Cookbook\n\n## 打包\n\n- **[脚本] 示例主题（{PAST} 实机）**：正文。\n"
    cases.append((mismatch_body, 1, "entry stage mismatch section -> fail"))

    # malformed entry (no body)
    malformed_body = f"# Cookbook\n\n## 脚本\n\n- **[脚本] 示例主题（{PAST} 实机）**：\n"
    cases.append((malformed_body, 1, "empty body -> fail"))

    # unknown stage heading
    unknown_head = f"# Cookbook\n\n## 未知\n\n{ok_entry}\n"
    cases.append((unknown_head, 1, "unknown stage heading -> fail"))

    failed = 0
    with tempfile.TemporaryDirectory() as td:
        for i, (content, expected, desc) in enumerate(cases):
            p = Path(td) / f"cookbook-{i}.md"
            p.write_text(content, encoding="utf-8")
            checked, errors = _scan(p)
            actual = 1 if errors else 0
            if actual == expected:
                print(f"  ok: {desc}")
            else:
                print(f"  ✗ {desc}: expected exit {expected}, got {actual} "
                      f"({' ; '.join(errors)})")
                failed = 1
    if failed == 0:
        print("== verify-cookbook self-test passed ==")
    else:
        print("== verify-cookbook self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_PATH
    checked, errors = _scan(path)
    print(f"Checked {checked} cookbook entries")
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
