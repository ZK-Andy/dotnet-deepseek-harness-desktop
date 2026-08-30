#!/usr/bin/env python3
"""Verify Agent Note (ADR) format: header block, skeleton, status-directory consistency,
plus file/date-name machine checks.

Checks, for every .md under .agents/notes/ (excluding archived/ and .zh.md files):
  1. Line 1 is "# Agent Note: <title>"; the Status line follows the title
     (a blank line between title and Status is allowed — matches the real
     deepseek-harness note convention)
  2. Status value matches the lifecycle folder (proposed/implemented/rejected)
  3. Required skeleton sections exist (## Problem, ## Alternatives considered,
     plus lifecycle-specific: ## Decision/## Consequences for implemented,
     ## Proposal for proposed)
  4. implemented notes must NOT contain spec-speak headings
     (## Proposal / ## Plan / ## Migration plan / ## Acceptance criteria)
  5. File/path naming (ADR naming rule, see proposed ADR
     2026-08-27-adr-naming-and-script-pitfall-records.md):
       - path is exactly <lifecycle>/<class>/<name>.md (top-level README exempt)
       - lifecycle ∈ {proposed, implemented, rejected} — matches Status (reused)
       - class ∈ {feature, bug-fix, simplification, architecture, process, testing}
       - <name> = yyyy-mm-dd-<slug>.md where date is a real calendar day and
         does not exceed today (a same-day note is allowed), and slug is kebab-case.

Usage: python3 verify-adr-format.py [notes_root]
       python3 verify-adr-format.py --self-test   # offline fixture self-check
Exit code 0 = pass, 1 = violations found.
"""

import datetime
import re
import sys
from pathlib import Path

HEADER_RE = re.compile(r"^# Agent Note: .+$")
STATUS_RE = re.compile(r"^Status: (proposed|implemented|rejected(?: — .+)?)$")
STATUS_BY_DIR = {"proposed": "proposed", "implemented": "implemented", "rejected": "rejected"}
BANNED_IN_IMPLEMENTED = ("## Proposal", "## Plan", "## Migration plan", "## Acceptance criteria")
REQUIRED_ALL = ("## Problem", "## Alternatives considered")
REQUIRED_IMPLEMENTED = ("## Decision", "## Consequences")
REQUIRED_PROPOSED = ("## Proposal",)

# --- ADR naming rule (machine-checked) ---
CLASS_SET = ("feature", "bug-fix", "simplification", "architecture", "process", "testing")
LIFECYCLE_SET = tuple(STATUS_BY_DIR)
NAME_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})-([a-z0-9]+(?:-[a-z0-9]+)*)\.md$")
# A top-level README sits directly under notes root; everything else is 3 parts deep.
TOP_LEVEL_EXEMPT = {"README.md"}


def _validate_name(rel: Path) -> list[str]:
    """Verify the ADR file/path naming rule for a non-top-level note."""
    errors: list[str] = []
    parts = rel.parts
    if len(parts) != 3:
        errors.append(f"{rel}: path must be exactly <lifecycle>/<class>/<name>.md "
                      "(3 segments; got {len(parts)})")
        return errors

    lifecycle, cls, name = parts
    if lifecycle not in LIFECYCLE_SET:
        errors.append(f"{rel}: lifecycle '{lifecycle}' not in {list(LIFECYCLE_SET)}")
    if cls not in CLASS_SET:
        errors.append(f"{rel}: class '{cls}' not in {list(CLASS_SET)}")

    m = NAME_RE.match(name)
    if m is None:
        errors.append(f"{rel}: filename must be 'yyyy-mm-dd-<kebab-slug>.md' "
                      "(lowercase, hyphen-separated words; no uppercase/underscore/other)")
        return errors

    date_str = m.group(1)
    try:
        note_date = datetime.date.fromisoformat(date_str)
    except ValueError:
        errors.append(f"{rel}: '{date_str}' is not a real calendar date")
        return errors

    # 时区容差：作者本地日期可领先/落后 UTC 至多 1 天（UTC+14 早 / UTC-12 晚），故允许
    # note_date ≤ today_utc+1。保留"不晚于今日"意图（拦截真未来/明显错日），同时容忍
    # 同日在不同时区的合法情况——CI runner 是 UTC，作者本地可能已跨到次日。
    today_utc = datetime.datetime.now(datetime.timezone.utc).date()
    if note_date > today_utc + datetime.timedelta(days=1):
        errors.append(f"{rel}: date '{date_str}' is after today ({today_utc})")
    if note_date.year < 1970:
        errors.append(f"{rel}: date '{date_str}' is before the epoch (1970)")
    return errors


def _scan(root: Path) -> tuple[int, list[str]]:
    """Return (checked_count, errors) for a real notes tree."""
    errors: list[str] = []
    checked = 0
    for note in sorted(root.rglob("*.md")):
        rel = note.relative_to(root)
        parts = rel.parts
        if "archived" in parts or note.name.endswith(".zh.md"):
            continue
        if note.name in TOP_LEVEL_EXEMPT:
            continue
        lifecycle = parts[0] if parts else ""
        if lifecycle not in STATUS_BY_DIR:
            continue
        checked += 1

        # 5. file/path naming rule (independent of the content checks below)
        errors.extend(_validate_name(rel))

        text = note.read_text(encoding="utf-8")
        lines = text.splitlines()

        if not lines or not HEADER_RE.match(lines[0]):
            errors.append(f"{rel}: line 1 must be '# Agent Note: <title>'")
        # 真实约定（deepseek 仓库实际笔记）：标题后可空一行，再跟 Status 行。
        status_line = next((l for l in lines[1:] if l.strip()), "")
        status_m = STATUS_RE.match(status_line)
        if status_m is None:
            errors.append(f"{rel}: must contain 'Status: <proposed|implemented|rejected>' after the title")
        elif status_m.group(1) != lifecycle:
            errors.append(f"{rel}: Status '{status_m.group(1)}' "
                          f"mismatches folder '{lifecycle}'")

        for sec in REQUIRED_ALL:
            if not any(l.strip() == sec for l in lines):
                errors.append(f"{rel}: missing required section '{sec}'")

        if lifecycle == "implemented":
            for sec in REQUIRED_IMPLEMENTED:
                if not any(l.strip() == sec for l in lines):
                    errors.append(f"{rel}: missing required section '{sec}'")
            for banned in BANNED_IN_IMPLEMENTED:
                if any(l.strip() == banned for l in lines):
                    errors.append(f"{rel}: implemented note must not contain '{banned}'")
        elif lifecycle == "proposed":
            for sec in REQUIRED_PROPOSED:
                if not any(l.strip() == sec for l in lines):
                    errors.append(f"{rel}: missing required section '{sec}'")

    return checked, errors


def _self_test(root: Path) -> int:
    """Offline fixture self-check: build a temp notes tree with conforming and
    violating files, assert the validator flags exactly the expected ones."""
    import tempfile

    OK_BODY = (
        "# Agent Note: sample\n\n"
        "Status: {status}\n\n"
        "## Problem\n\nbackground\n\n"
        "## {decision_sec}\n\ndecision\n\n"
        "## Alternatives considered\n\n- x\n\n"
        "## Consequences\n\nconsequences\n"
    )

    def write_status(dirpath: Path, name: str, status: str):
        body = OK_BODY.format(status=status, decision_sec="Decision")
        (dirpath / name).write_text(body, encoding="utf-8")

    cases = []  # (tmp_root, notes_root, expected_exit, description)
    with tempfile.TemporaryDirectory() as td:
        t = Path(td)

        # case A: fully conforming tree → exit 0
        conform = t / "conform"
        (conform / "implemented" / "feature").mkdir(parents=True)
        write_status(conform / "implemented" / "feature", "2026-08-27-naming-ok.md", "implemented")
        cases.append((conform, 0, "conforming tree -> pass"))

        # case B: bad class segment → exit 1
        badclass = t / "badclass"
        (badclass / "implemented" / "refactor").mkdir(parents=True)
        write_status(badclass / "implemented" / "refactor", "2026-08-27-naming-ok.md", "implemented")
        cases.append((badclass, 1, "invalid class 'refactor' -> fail"))

        # case C: uppercase in slug → exit 1
        badslug = t / "badslug"
        (badslug / "implemented" / "feature").mkdir(parents=True)
        write_status(badslug / "implemented" / "feature", "2026-08-27-Naming-Ok.md", "implemented")
        cases.append((badslug, 1, "uppercase in filename -> fail"))

        # case D: future date → exit 1（相对 UTC 今天 +2，恒超过 +1 容差，跨时区亦确定失败）
        futuredate = t / "futuredate"
        (futuredate / "implemented" / "feature").mkdir(parents=True)
        future = (datetime.datetime.now(datetime.timezone.utc).date() + datetime.timedelta(days=2)).isoformat()
        write_status(futuredate / "implemented" / "feature", f"{future}-naming-ok.md", "implemented")
        cases.append((futuredate, 1, "date after today -> fail"))

        # case E: invalid calendar day → exit 1
        baddate = t / "baddate"
        (baddate / "implemented" / "feature").mkdir(parents=True)
        write_status(baddate / "implemented" / "feature", "2026-02-31-naming-ok.md", "implemented")
        cases.append((baddate, 1, "invalid calendar date -> fail"))

        # case F: wrong segment count (class dir missing) → exit 1
        badcount = t / "badcount"
        (badcount / "implemented").mkdir(parents=True)
        write_status(badcount / "implemented", "2026-08-27-naming-ok.md", "implemented")
        cases.append((badcount, 1, "path not 3 segments -> fail"))

        failed = 0
        for notes_root, expected, desc in cases:
            checked, errors = _scan(notes_root)
            actual = 1 if errors else 0
            if actual == expected:
                print(f"  ok: {desc}")
            else:
                print(f"  ✗ {desc}: expected exit {expected}, got {actual} ({'; '.join(errors)})")
                failed = 1
        if failed == 0:
            print("== verify-adr-format self-test passed ==")
        else:
            print("== verify-adr-format self-test failed ==", file=sys.stderr)
        return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test(Path("."))

    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(".agents/notes")
    if not root.is_dir():
        print(f"SKIP: {root} does not exist (no Agent Notes tree)")
        return 0

    checked, errors = _scan(root)
    print(f"Checked {checked} Agent Notes")
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
