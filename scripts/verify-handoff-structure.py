#!/usr/bin/env python3
"""Verify the HANDOFF structure stays lean and layered.

HANDOFF is a gitignored local working document family (single source of
truth for background / location / current state / todo / start steps):

  HANDOFF.md        — entry file: stable sections + *summary* rolling window
  HANDOFF-todos.md  — action area: all todo items, lifecycle-constrained

The 2026-08-27 split added a bounded rolling window; the 2026-08-31 split
(split-governance) went further because a window cap alone did not bound
growth — entries were 800-1400 chars and completed todos never shrank.
This gate gives the gitignored layer a machine backstop:

  * The `## 交接更新记录` window keeps only recent session-batch *summaries*
    (date | type | commit/ADR pointer | one-line conclusion); full narrative
    is archived to `.plan/journal/<YYYY-MM>-session-journal.md`. Enforced:
    entry count <= --max-window (default 24) and each entry <= --max-entry
    chars (default 260).
  * The `## 待办` section in HANDOFF.md is a pointer; the real action area
    is HANDOFF-todos.md. Enforced: file exists and is referenced; open items
    (`[ ]`) <= --max-open and <= --max-open-chars; closed items (`[x]`) <=
    --max-closed-chars (one-line pointers).
  * Closed pointers (`[x]`) live in a bounded recent window of --max-closed
    items (default 24, the same order as the HANDOFF.md summary window). Older
    pointers are archived to `.plan/todos-archive/<YYYY-MM>-todos-archive.md`.
    The todos file and its archive volumes are one pair: every volume it names
    must exist, and a populated archive directory must be named by the file.
  * Required body sections must exist; the journal archive pointer must be
    present and its referenced volume must exist.

Usage: python3 scripts/verify-handoff-structure.py [--handoff PATH] [--max-window N] ...
       python3 scripts/verify-handoff-structure.py --self-test   # offline fixtures
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

DEFAULT_HANDOFF = "HANDOFF.md"
DEFAULT_TODOS = "HANDOFF-todos.md"
DEFAULT_MAX_WINDOW = 24
DEFAULT_MAX_ENTRY = 260
DEFAULT_MAX_OPEN = 16
DEFAULT_MAX_CLOSED = 24
DEFAULT_MAX_OPEN_CHARS = 340
DEFAULT_MAX_CLOSED_CHARS = 220

HEADER_SECTIONS = ("背景", "位置", "当前状态", "待办", "开始步骤")
ENTRY_RE = re.compile(r"^- \d{4}-\d{2}-\d{2}｜")
SECTION_RE = re.compile(r"^## (?P<name>.+?)\s*$")
JOURNAL_RE = re.compile(r"\.plan/journal/[\w\u4e00-\u9fff-]+\.md")
TODOS_ARCHIVE_RE = re.compile(r"\.plan/todos-archive/[\w\u4e00-\u9fff-]+\.md")
TODO_RE = re.compile(r"^- \[([ x])\] ")
TODOS_REF_RE = re.compile(r"HANDOFF-todos\.md")
# Single home for the journal volume name, shared by _scan resolution and the
# self-test fixtures so the "one fact, one home" rule holds across both.
JOURNAL_VOLUME = "2026-08-session-journal.md"
# Same contract for the cold-archive volume name the todos fixtures point at.
TODOS_ARCHIVE_VOLUME = "2026-09-todos-archive.md"


def _scan(handoff: Path, todos_path: Path, max_window: int, max_entry: int,
          max_open: int, max_closed: int, max_open_chars: int,
          max_closed_chars: int) -> tuple[int, list[str]]:
    """Return (window_count, errors).

    The journal volume, the todos file and the todos archive volumes live under
    the HANDOFF's own parent (repo root when HANDOFF is at the repo root), so we
    resolve them from `handoff.parent` rather than the current working directory
    or the todos file's own directory. This keeps `--handoff` pointing at a
    HANDOFF elsewhere (its intended use) from false-failing, and keeps the
    archive anchor the same one the journal volume uses.
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

    # 2) rolling-window entries: count bounded + each a short summary
    window_count = 0
    in_window = False
    for i, line in enumerate(lines, 1):
        if line.strip().startswith("## 交接更新记录"):
            in_window = True
            continue
        if in_window and SECTION_RE.match(line.strip()):
            in_window = False
            continue
        if in_window and ENTRY_RE.match(line.strip()):
            window_count += 1
            if len(line) > max_entry:
                errors.append(
                    f"{handoff}: line {i}: window entry exceeds {max_entry} "
                    f"chars ({len(line)}). Summaries only — full narrative "
                    f"goes to .plan/journal/.")
    if window_count > max_window:
        errors.append(
            f"{handoff}: 交接更新记录 has {window_count} entries, exceeds max "
            f"window {max_window}. Archive the oldest summaries to "
            f".plan/journal/ before adding to HANDOFF."
        )

    # 3) `## 待办` section must point at the todos file
    todos_found = False
    in_todo = False
    for line in lines:
        if line.strip().startswith("## 待办"):
            in_todo = True
            continue
        if in_todo and SECTION_RE.match(line.strip()):
            break
        if in_todo and TODOS_REF_RE.search(line):
            todos_found = True
    if not todos_found:
        errors.append(
            f"{handoff}: `## 待办` section must reference the todos file "
            f"({todos_path.name}).")

    # 4) todos file: exists + item counts/compression limits
    if todos_path.is_file():
        todo_errors = _scan_todos(todos_path, handoff.parent, max_open,
                                  max_closed, max_open_chars,
                                  max_closed_chars)
        errors.extend(todo_errors)
    elif todos_found:
        errors.append(f"{handoff}: referenced todos file missing: {todos_path}")
    else:
        errors.append(f"{handoff}: todos file missing: {todos_path} "
                      f"(create {todos_path.name} for the action area).")

    # 5) journal archive pointer present + referenced volume exists
    journal_ref = JOURNAL_RE.search(text)
    if not journal_ref:
        errors.append(
            f"{handoff}: missing archive pointer to `.plan/journal/<YYYY-MM>-"
            f"session-journal.md` (add a '## 会话叙事档案' note so old narrative "
            f"has a home).")
    else:
        vol = journal_ref.group(0).split("/")[-1]
        jpath = handoff.parent / ".plan" / "journal" / vol
        if not jpath.is_file():
            errors.append(f"{handoff}: referenced journal volume '{vol}' not "
                          f"found at {jpath}")
    return window_count, errors


def _scan_todos(path: Path, root: Path, max_open: int, max_closed: int,
                max_open_chars: int, max_closed_chars: int) -> list[str]:
    """Item budgets plus the cold-archive pointer/volume pair.

    `root` is the HANDOFF's parent (the anchor `_scan` already uses for the
    journal volume), not the todos file's own directory: the two agree in the
    normal layout, and pinning both to one anchor keeps a `--todos` pointing
    elsewhere from silently changing where the archive is looked up.
    """
    errors: list[str] = []
    text = path.read_text(encoding="utf-8")
    open_count = closed_count = 0
    for i, line in enumerate(text.splitlines(), 1):
        m = TODO_RE.match(line)
        if not m:
            continue
        state = m.group(1)
        if state == " ":
            open_count += 1
            if len(line) > max_open_chars:
                errors.append(
                    f"{path}: line {i}: open todo exceeds {max_open_chars} "
                    f"chars ({len(line)}). Keep action + trigger + pointer.")
        else:
            closed_count += 1
            if len(line) > max_closed_chars:
                errors.append(
                    f"{path}: line {i}: closed todo exceeds {max_closed_chars} "
                    f"chars ({len(line)}). Compress to a one-line pointer — "
                    f"detail lives in journal/ADR.")
    if open_count > max_open:
        errors.append(f"{path}: {open_count} open items exceed max {max_open} "
                      f"— finish or prune before adding more.")
    if closed_count > max_closed:
        errors.append(
            f"{path}: {closed_count} closed pointers exceed the recent window "
            f"of {max_closed} — move the oldest into "
            f".plan/todos-archive/<YYYY-MM>-todos-archive.md and leave a "
            f"one-line pointer to that volume.")

    # Cold-archive pair, both directions: every volume the file names must
    # exist, and a populated archive directory must be named by the file — a
    # dropped pointer would otherwise orphan that volume silently. Refs are
    # de-duplicated (a Markdown link names its target twice) so one missing
    # volume yields one error.
    refs = list(dict.fromkeys(TODOS_ARCHIVE_RE.findall(text)))
    archive_dir = root / ".plan" / "todos-archive"
    for ref in refs:
        if not (root / ref).is_file():
            errors.append(f"{path}: referenced todos archive '{ref}' not found "
                          f"at {root / ref}")
    if not refs and any(archive_dir.glob("*.md")):
        errors.append(
            f"{path}: {archive_dir} holds archived volumes but this file names "
            f"none — add a one-line pointer to the current volume.")
    return errors


def _self_test() -> int:
    """Offline fixture self-check built from synthetic HANDOFF trees."""
    import tempfile

    short = "- 2026-08-31｜**会话 t**：一句结论。"
    long_entry = "- 2026-08-31｜**会话 t**：" + "长" * 300
    open_todo = "- [ ] 待办甲，行动+触发+指针。"
    closed_todo = "- [x] 已办乙，一行指针。"
    archive_ref = f"> 冷归档在 `.plan/todos-archive/{TODOS_ARCHIVE_VOLUME}`。"

    @dataclass
    class Case:
        """One synthetic HANDOFF tree plus the exit code it must produce.

        A case states only what it varies; the rest falls back to the
        conforming fixture.
        """

        desc: str
        expected: int
        entries: list[str] = field(default_factory=lambda: [short])
        journal_ref: bool = True
        sections: bool = True
        journal_file: bool = True
        todos: bool = True
        todo_lines: list[str] = field(default_factory=lambda: [open_todo])
        todos_ref: bool = True
        archive_file: bool = False

    def build(tree: Path, case: Case) -> None:
        (tree / ".plan" / "journal").mkdir(parents=True, exist_ok=True)
        lines = ["# HANDOFF — test\n", "\n", "## 交接更新记录\n", "\n"]
        if case.journal_ref:
            lines.append(
                f"> 滚动窗口有界；旧叙事归档 `.plan/journal/{JOURNAL_VOLUME}`。\n")
        lines.append("\n")
        for e in case.entries:
            lines.append(e + "\n")
        if case.sections:
            lines.append("\n## 背景\n\n正文。\n")
            lines.append("\n## 位置\n\n正文。\n")
            lines.append("\n## 当前状态\n\n正文。\n")
            lines.append("\n## 待办\n\n")
            if case.todos_ref:
                lines.append("> 行动区在 [HANDOFF-todos.md](HANDOFF-todos.md)。\n")
            lines.append("\n## 开始步骤\n\n正文。\n")
        (tree / "HANDOFF.md").write_text("".join(lines), encoding="utf-8")
        if case.journal_file:
            (tree / ".plan" / "journal" / JOURNAL_VOLUME).write_text(
                "# journal\n", encoding="utf-8")
        if case.archive_file:
            (tree / ".plan" / "todos-archive").mkdir(parents=True, exist_ok=True)
            (tree / ".plan" / "todos-archive" / TODOS_ARCHIVE_VOLUME).write_text(
                "# todos archive\n", encoding="utf-8")
        if case.todos:
            (tree / "HANDOFF-todos.md").write_text(
                "".join(t + "\n" for t in case.todo_lines), encoding="utf-8")

    cases = [
        Case("conforming (summary window + todos file)", 0,
             todo_lines=[open_todo, closed_todo]),
        Case("30 entries exceeds max window -> fail", 1,
             entries=[short] * 30, todo_lines=[open_todo]),
        Case("over-long window entry -> fail", 1,
             entries=[long_entry], todo_lines=[open_todo]),
        Case("todos file missing -> fail", 1, todos=False, todos_ref=False),
        Case("over-long open todo -> fail", 1,
             todo_lines=["- [ ] " + "长" * 500]),
        Case("todos file not referenced from 待办 -> fail", 1,
             todos_ref=False, todo_lines=[open_todo]),
        Case("missing journal pointer -> fail", 1, journal_ref=False,
             todo_lines=[open_todo]),
        Case("missing body sections -> fail", 1, sections=False,
             todo_lines=[open_todo]),
        Case("referenced journal volume missing -> fail", 1,
             journal_file=False, todo_lines=[open_todo]),
        Case("too many open todos -> fail", 1,
             todo_lines=[f"- [ ] 待办 {n}。" for n in range(25)]),
        Case("no open items, closed ok -> pass", 0, todo_lines=[closed_todo]),
        Case("closed pointers at window cap -> pass", 0,
             todo_lines=[f"- [x] 已办 {n}。" for n in range(DEFAULT_MAX_CLOSED)]),
        Case("closed pointers over window -> fail", 1,
             todo_lines=[f"- [x] 已办 {n}。"
                         for n in range(DEFAULT_MAX_CLOSED + 1)]),
        Case("archive pointer resolves -> pass", 0,
             todo_lines=[closed_todo, archive_ref], archive_file=True),
        Case("archive pointer without volume -> fail", 1,
             todo_lines=[closed_todo, archive_ref]),
        Case("archive volume without pointer -> fail", 1,
             todo_lines=[closed_todo], archive_file=True),
    ]

    failed = 0
    with tempfile.TemporaryDirectory() as td:
        for i, case in enumerate(cases):
            tree = Path(td) / f"tree-{i}"
            tree.mkdir(parents=True, exist_ok=True)
            build(tree, case)
            _, errors = _scan(tree / "HANDOFF.md", tree / "HANDOFF-todos.md",
                              DEFAULT_MAX_WINDOW, DEFAULT_MAX_ENTRY,
                              DEFAULT_MAX_OPEN, DEFAULT_MAX_CLOSED,
                              DEFAULT_MAX_OPEN_CHARS, DEFAULT_MAX_CLOSED_CHARS)
            actual = 1 if errors else 0
            if actual == case.expected:
                print(f"  ok: {case.desc}")
            else:
                print(f"  ✗ {case.desc}: expected exit {case.expected}, got "
                      f"{actual} ({' ; '.join(errors)})")
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
    parser.add_argument("--todos", default=DEFAULT_TODOS)
    parser.add_argument("--max-window", type=int, default=DEFAULT_MAX_WINDOW)
    parser.add_argument("--max-entry", type=int, default=DEFAULT_MAX_ENTRY)
    parser.add_argument("--max-open", type=int, default=DEFAULT_MAX_OPEN)
    parser.add_argument("--max-closed", type=int, default=DEFAULT_MAX_CLOSED)
    parser.add_argument("--max-open-chars", type=int, default=DEFAULT_MAX_OPEN_CHARS)
    parser.add_argument("--max-closed-chars", type=int, default=DEFAULT_MAX_CLOSED_CHARS)
    args = parser.parse_args()

    handoff = Path(args.handoff)
    todos = handoff.parent / args.todos
    count, errors = _scan(handoff, todos, args.max_window, args.max_entry,
                          args.max_open, args.max_closed, args.max_open_chars,
                          args.max_closed_chars)
    print(f"HANDOFF 交接更新记录 entries: {count}")
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())