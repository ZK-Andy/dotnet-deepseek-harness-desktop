#!/usr/bin/env python3
"""Verify the HANDOFF structure (HANDOFF.md) stays lean and layered.

HANDOFF.md is a gitignored local working document (single source of truth for
background / location / current state / todo / start steps). It is NOT under
`verify-md-links` / `verify-doc-budgets` scope by default, so nothing currently
bounds its growth. This gate gives the gitignored layer a machine backstop:

Each session's narrative (the `## 交接更新记录` block) is a *bounded rolling
window*: it keeps only recent session batches. Older narrative is archived to
`.plan/journal/<YYYY-MM>-session-journal.md`. Durable decisions never live here
— they live in git-tracked ADRs (`.agents/notes/`), the cookbook, AGENTS.md,
and README. So the window must never accumulate unbounded.

Checks (when HANDOFF.md is present — absent, e.g. clean CI, passes silently):
  1. The `## 交接更新记录` block must not exceed `--max-window` entries
     (default 40). Exceeding it means the newest entry did not archive the
     oldest; fail loud so the agent moves old narrative to the journal.
  2. Required body sections must all exist:
     `## 背景`, `## 位置`, `## 当前状态`, `## 待办`, `## 开始步骤`.
  3. A journal archive pointer must be present (a `` ` `` reference to
     `.plan/journal/<YYYY-MM>-session-journal.md`), and that referenced volume
     must exist under `.plan/journal/`.

Usage: python3 scripts/verify-handoff-structure.py [--handoff PATH] [--max-window N]
       python3 scripts/verify-handoff-structure.py --self-test   # offline fixtures
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import sys
from pathlib import Path

DEFAULT_HANDOFF = "HANDOFF.md"
DEFAULT_MAX_WINDOW = 40

HEADER_SECTIONS = ("背景", "位置", "当前状态", "待办", "开始步骤")
ENTRY_RE = re.compile(r"^- \d{4}-\d{2}-\d{2}｜")
SECTION_RE = re.compile(r"^## (?P<name>.+?)\s*$")
JOURNAL_RE = re.compile(r"\.plan/journal/[\w\u4e00-\u9fff-]+\.md")
# Single home for the journal volume name, shared by _scan resolution and the
# self-test fixtures so the "one fact, one home" rule holds across both.
JOURNAL_VOLUME = "2026-08-session-journal.md"


def _scan(handoff: Path, max_window: int) -> tuple[int, list[str]]:
    """Return (window_count, errors).

    The journal volume lives under the HANDOFF's own parent (repo root when
    HANDOFF is at the repo root), so we resolve it from `handoff.parent`
    rather than the current working directory. This keeps `--handoff` pointing
    at a HANDOFF elsewhere (its intended use) from false-failing.
    """
    if not handoff.is_file():
        return 0, []  # absent in clean CI: nothing to guard

    errors: list[str] = []
    text = handoff.read_text(encoding="utf-8")
    lines = text.splitlines()

    # 1) required body sections present
    seen_sections: set[str] = set()
    for line in lines:
        m = SECTION_RE.match(line.strip())
        if m:
            # tolerate a parenthetical suffix, e.g. "## 待办（第二步 …）"
            name = m.group("name").strip()
            base = name.split("（")[0].split("(")[0].strip()
            seen_sections.add(base)
    missing = [s for s in HEADER_SECTIONS if s not in seen_sections]
    if missing:
        errors.append(f"{handoff}: missing required section(s): {missing}")

    # 2) rolling-window entry count within the bounded window
    window_count = 0
    in_window = False
    for line in lines:
        if line.strip() == "## 交接更新记录":
            in_window = True
            continue
        if in_window and SECTION_RE.match(line.strip()):
            in_window = False
            continue
        if in_window and ENTRY_RE.match(line.strip()):
            window_count += 1
    if window_count > max_window:
        errors.append(
            f"{handoff}: 交接更新记录 has {window_count} entries, exceeds max "
            f"window {max_window}. Archive the oldest narrative to "
            f".plan/journal/ before adding to HANDOFF."
        )

    # 3) journal archive pointer present + referenced volume exists
    journal_ref = JOURNAL_RE.search(text)
    if not journal_ref:
        errors.append(
            f"{handoff}: missing archive pointer to `.plan/journal/<YYYY-MM>-"
            f"session-journal.md` (add a '## 会话叙事档案' note so old narrative "
            f"has a home)."
        )
    else:
        vol = journal_ref.group(0).split("/")[-1]
        jpath = handoff.parent / ".plan" / "journal" / vol
        if not jpath.is_file():
            errors.append(f"{handoff}: referenced journal volume '{vol}' not "
                          f"found at {jpath}")
    return window_count, errors


def _self_test() -> int:
    """Offline fixture self-check built from synthetic HANDOFF trees."""
    import tempfile

    def build(tree: Path, entries: int, journal: bool, sections: bool,
              journal_file: bool) -> None:
        (tree / ".plan" / "journal").mkdir(parents=True, exist_ok=True)
        lines = ["# HANDOFF — test\n", "\n", "## 交接更新记录\n", "\n"]
        if journal:
            lines.append(
                f"> 滚动窗口有界；旧叙事归档 `.plan/journal/{JOURNAL_VOLUME}`。\n")
        lines.append("\n")
        for i in range(1, entries + 1):
            lines.append(f"- 2026-08-27｜**会话编号 {i}**：一句话结论。\n")
        if sections:
            for s in ("背景", "位置", "当前状态", "待办", "开始步骤"):
                lines.append(f"\n## {s}\n\n正文。\n")
        (tree / "HANDOFF.md").write_text("".join(lines), encoding="utf-8")
        if journal_file:
            (tree / ".plan" / "journal" / JOURNAL_VOLUME).write_text(
                "# journal\n", encoding="utf-8")

    cases = []  # (entries, journal_ref, sections, journal_exists, expected, desc)
    cases.append((5, True, True, True, 0, "conforming (5 entries, pointer, sections) -> pass"))
    cases.append((45, True, True, True, 1, "45 entries exceeds max window -> fail"))
    cases.append((5, False, True, True, 1, "missing journal pointer -> fail"))
    cases.append((5, True, False, True, 1, "missing body sections -> fail"))
    cases.append((5, True, True, False, 1, "referenced journal volume missing -> fail"))

    failed = 0
    with tempfile.TemporaryDirectory() as td:
        for i, (entries, jref, secs, jfile, expected, desc) in enumerate(cases):
            tree = Path(td) / f"tree-{i}"
            tree.mkdir(parents=True, exist_ok=True)
            build(tree, entries, jref, secs, jfile)
            count, errors = _scan(tree / "HANDOFF.md", DEFAULT_MAX_WINDOW)
            actual = 1 if errors else 0
            if actual == expected:
                print(f"  ok: {desc}")
            else:
                print(f"  ✗ {desc}: expected exit {expected}, got {actual} "
                      f"({' ; '.join(errors)})")
                failed = 1
    if failed == 0:
        print("== verify-handoff-structure self-test passed ==")
    else:
        print("== verify-handoff-structure self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(description="Verify HANDOFF.md structure")
    parser.add_argument("--handoff", default=DEFAULT_HANDOFF)
    parser.add_argument("--max-window", type=int, default=DEFAULT_MAX_WINDOW)
    args = parser.parse_args()

    count, errors = _scan(Path(args.handoff), args.max_window)
    print(f"HANDOFF 交接更新记录 entries: {count}")
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
