#!/usr/bin/env python3
"""Verify that a change's review tier is honored (review-tier-escape-proofing).

Mechanizes the review-tier decision that used to be the executor's discretion
(the 2026-09-03 escape: a compose-root refactor whose ADR explicitly promised
a full three-way review was run through a single R2 light review because the
general "zero-behavior-change => light review" rule won by default). Tier
classification rules live in the ADR `.agents/notes/implemented/process/
2026-09-03-review-tier-escape-proofing.md`; this script is their mechanical
enforcement (single source of truth for the path patterns).

A diff touching any FULL tier path requires review evidence travelling WITH
the change before it may be committed/pushed; without evidence the gate fails
(`--enforce`). Evidence = an ADR under .agents/notes that is part of THIS
change set (same staged set / same base..HEAD range; in --staged mode read from
the index, the tree being committed) and whose header zone carries a valid
`Review:` line ADDED by this change — a pre-existing Review: line merely
carried along in the diff does not count, whether its ADR is touched by the
batch or removed and re-created under a new name by it (rule:
`.agents/notes/implemented/process/2026-09-13-review-evidence-freshness-gate.md`).

An undeterminable diff is a violation, not an empty change set: when the base
ref cannot be resolved (`--since` after a force-push / rebase / shallow clone)
or git fails, the gate reports `cannot determine the change set` and `--enforce`
exits 1. A gate that silently passes on an unresolvable base is not a gate.

Diff scope (caller picks the mode matching the git moment):
  --staged            index vs HEAD        -> pre-commit
  --since <base-ref>  <base>..HEAD commits -> pre-push / CI (outgoing)
  (default)           working tree         -> ad-hoc local check

Tier classification (no executor discretion):

  FULL (full three-way review R1/R2/R3 required):
    - compose-root core: any-depth DesktopBootstrap*.cs / Program.cs under src/
    - gate criteria touched: ArchitectureTests.cs, verify-*.py / verify-*.sh,
      .editorconfig, .githooks/** (removing a hook call must itself be FULL)
    - proposed ADR added/changed whose body commits to "三重审核"
      (the ADR's own promised review tier outranks the general default)
    - behavior-contract surface outside src/tests: .github/workflows/**,
      resources/**, templates/**, docs/**
  LIGHT (light review R2 may suffice): everything else.

Review evidence format (header zone of an implemented ADR):
    Review: FULL/yyyy-mm-dd/R1=ok R2=ok R3=ok
  - the ADR must be part of this change set, Status must be implemented (a
    proposed ADR cannot self-certify), and the line must satisfy the freshness
    rule stated above

Default is report-only (exit 0). `--enforce` exits 1 when a FULL-tier diff
lacks review evidence. `--self-test` runs offline fixtures.

Companion gate: before any review lane is launched the main session must write
a bounded brief per lane under `.review-briefs/` and run
`scripts/verify-review-brief.py --enforce` (FULL => R1/R2/R3 briefs, LIGHT =>
R2). This script gates *evidence after review* (commit/push); the brief gate
gates *task bounding before review* (local launch). See
`.agents/notes/implemented/process/2026-08-31-review-scope-narrowing.md` §4.

Consumption contract: `scripts/verify-review-brief.py` imports this module's
private helpers `_repo_changed_paths` and `_classify` (lane derivation for the
brief gate, single source of truth for the tier). That import is only safe
because this module has NO top-level side effects beyond constant/regex
definition (`if __name__ == "__main__"` guards main()). Keep it that way:
adding a top-level git call or file read would silently break the brief gate's
lane derivation. The brief gate also inherits the FULL classification of
`verify-*.py` path changes, so editing THIS script is FULL-tier by design.

Usage:
    python3 scripts/verify-review-tier.py [--repo ROOT] [--staged|--since BASE] [--enforce]
    python3 scripts/verify-review-tier.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import datetime as _dt
import re
import subprocess
import sys
import tempfile
from pathlib import Path

NOTES_DIR = ".agents/notes"
# FULL/<date>/R1=ok R2=ok R3=ok — values strictly checked (a fail/abort marks nothing).
REVIEW_LINE_RE = re.compile(
    r"^Review:\s*FULL\s*/\s*(?P<date>\d{4}-\d{2}-\d{2})\s*/\s*"
    r"R1=ok\s+R2=ok\s+R3=ok$")

# FULL-tier path triggers. Predicates take (path-string, pathlib.Path) and
# return True when the path forces the FULL tier.
FULL_TRIGGERS = (
    ("compose-root", lambda rel, p: (p.name.startswith("DesktopBootstrap") or p.name == "Program.cs") and p.suffix == ".cs"),
    ("gate-criteria", lambda rel, p: p.name == "ArchitectureTests.cs"),
    ("gate-criteria", lambda rel, p: p.name.startswith("verify-") and p.suffix in (".py", ".sh")),
    ("gate-criteria", lambda rel, p: p.name == ".editorconfig"),
    ("gate-criteria", lambda rel, p: ".githooks" in p.parts),
    ("behavior-surface", lambda rel, p: rel.startswith(".github/workflows/")),
    ("behavior-surface", lambda rel, p: "resources" in p.parts),
    ("behavior-surface", lambda rel, p: "templates" in p.parts),
    ("behavior-surface", lambda rel, p: "docs" in p.parts),
)

FULL_TIER_WORDS = ("三重审核", "三重审核（R1/R2/R3", "三重审核（R1/R2/R3 串行）")


def _valid_date(s: str) -> bool:
    try:
        _dt.date.fromisoformat(s)
        return True
    except ValueError:
        return False


def _changed_paths_or_error(repo: Path, staged_only: bool = False,
                            since: str | None = None) -> tuple[list[str] | None, str | None]:
    """Changed paths for the selected git moment, or (None, error) — see
    _repo_changed_paths for the moments. An undeterminable diff (unreachable
    --since base, unborn HEAD, git failure) is an ERROR, never an empty change
    set: an empty set passes the gate, and the shapes that lose the base ref
    (force-push before-hash, rebase-orphaned base.sha, shallow clone) are exactly
    the ones where the FULL-tier gate must not fall silent."""
    out: set[str] = set()
    if staged_only:
        cmds = (["git", "diff", "--cached", "--name-only"],)
    elif since:
        cmds = (["git", "diff", "--name-only", f"{since}..HEAD"],)
    else:
        cmds = (
            ["git", "diff", "--name-only", "HEAD"],
            ["git", "diff", "--cached", "--name-only"],
            ["git", "diff", "--name-only"],
        )
    for cmd in cmds:
        r = subprocess.run(cmd, cwd=repo, capture_output=True, text=True, errors="replace", check=False)
        if r.returncode != 0:
            first = r.stderr.strip().splitlines()
            return None, (f"`{' '.join(cmd)}` failed (rc={r.returncode}): "
                          f"{first[0] if first else 'no stderr'}")
        out.update(x for x in r.stdout.splitlines() if x.strip())
    if not staged_only and not since:
        # untracked (not in HEAD, not ignored)
        r = subprocess.run(["git", "ls-files", "--others", "--exclude-standard"],
                           cwd=repo, capture_output=True, text=True, errors="replace", check=False)
        if r.returncode != 0:
            return None, f"`git ls-files --others` failed (rc={r.returncode})"
        out.update(x for x in r.stdout.splitlines() if x.strip())
    return sorted(out), None


def _repo_changed_paths(repo: Path, staged_only: bool = False,
                        since: str | None = None) -> list[str]:
    """Changed paths for the selected git moment:
    --staged => index vs HEAD; --since => <base>..HEAD; default => working tree.

    List form kept as the consumption contract for verify-review-brief.py (lane
    derivation). Callers that must fail loud on an undeterminable diff use
    _changed_paths_or_error; here the failure is dropped, so the brief gate's
    lane derivation degrades to LIGHT — the tier gate itself is the one that
    fails loud on the same condition."""
    paths, _err = _changed_paths_or_error(repo, staged_only, since)
    return paths or []


def _adr_commits_full(rel: str, repo: Path) -> bool:
    """A changed proposed ADR whose body commits to a full review forces FULL."""
    if not rel.endswith(".md") or rel.count("/") < 1 or not rel.startswith(NOTES_DIR):
        return False
    path = repo / rel
    try:
        text = path.read_text(encoding="utf-8")
    except Exception:
        return False
    return "Status: proposed" in text and any(w in text for w in FULL_TIER_WORDS)


def _classify(paths: list[str], repo: Path) -> tuple[bool, list[str]]:
    """Return (is_full_tier, reasons). No executor discretion."""
    full = False
    reasons: list[str] = []
    for rel in paths:
        p = Path(rel)
        matched = False
        for label, pred in FULL_TRIGGERS:
            try:
                hit = pred(rel, p)
            except Exception:
                hit = False
            if hit:
                if label not in reasons:
                    reasons.append(f"{label}: {rel}")
                matched = True
                full = True
                break
        if not matched and _adr_commits_full(rel, repo):
            full = True
            reasons.append(f"adr-promises-full: {rel}")
    return full, reasons


def _note_text(rel: str, repo: Path, staged_only: bool = False) -> str | None:
    """Note content as it will land: the index version in --staged mode (the tree
    being committed), else the file on disk. None = absent there."""
    if staged_only:
        r = subprocess.run(["git", "show", f":{rel}"], cwd=repo,
                           capture_output=True, text=True, errors="replace", check=False)
        return r.stdout if r.returncode == 0 else None
    try:
        return (repo / rel).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None  # listed as changed but gone from the working tree


def _header_zone(text: str) -> list[str]:
    """The note's header zone: everything above the first `## ` section heading
    (capped), where `Status:` and `Review:` live however long the front matter is."""
    lines = text.splitlines()[:60]
    for i, ln in enumerate(lines):
        if ln.startswith("## "):
            return lines[:i]
    return lines


def _inherited_review_lines(repo: Path, staged_only: bool = False,
                            since: str | None = None
                            ) -> tuple[set[tuple[str, str]], dict[str, set[str]]] | None:
    """Review: lines a note inherits from a note this change set deletes or
    renames away, read at the base revision. Two keys, because `git diff -U0 --
    <new path>` shows a rename destination as fully added: by note title (catches
    a heavy rewrite that defeats rename detection) and by rename destination path
    (catches a rename whose title the batch also changed). None = git could not
    determine the removed set; the caller fails loud rather than treating an
    unknown set as "nothing was inherited"."""
    base = since if since else "HEAD"
    if staged_only:
        cmd = ["git", "diff", "--cached", "--name-status", "-M"]
    elif since:
        cmd = ["git", "diff", "--name-status", "-M", f"{since}..HEAD"]
    else:
        cmd = ["git", "diff", "--name-status", "-M", "HEAD"]
    r = subprocess.run(cmd, cwd=repo, capture_output=True, text=True, errors="replace", check=False)
    if r.returncode != 0:
        return None
    gone: list[tuple[str, str | None]] = []
    for row in r.stdout.splitlines():
        fields = row.split("\t")
        if len(fields) < 2:
            continue
        status, old = fields[0], fields[1]
        if not (old.startswith(NOTES_DIR) and old.endswith(".md")):
            continue
        if status.startswith("D"):
            gone.append((old, None))
        elif status.startswith("R") and len(fields) >= 3:
            gone.append((old, fields[2]))
    by_title: set[tuple[str, str]] = set()
    by_path: dict[str, set[str]] = {}
    for old, dest in gone:
        # errors="replace": an undecodable pre-image must not abort the gate with a
        # traceback; replacement chars simply fail the Review: line match (fail closed)
        r = subprocess.run(["git", "show", f"{base}:{old}"], cwd=repo,
                           capture_output=True, text=True, errors="replace", check=False)
        if r.returncode != 0:
            continue  # pre-image unreadable at base: nothing to key the exclusion on
        lines = r.stdout.splitlines()
        title = next((ln.strip() for ln in lines if ln.startswith("# Agent Note:")), "")
        for ln in lines:
            stripped = ln.strip()
            m = REVIEW_LINE_RE.match(stripped)
            if not (m and _valid_date(m.group("date"))):
                continue
            by_title.add((title, stripped))
            if dest:
                by_path.setdefault(dest, set()).add(stripped)
    return by_title, by_path


def _added_lines_for(rel: str, repo: Path, staged_only: bool = False,
                     since: str | None = None) -> set[str] | None:
    """The lines ADDED to `rel` in the selected git moment (`git diff -U0`, the
    `+` lines minus the `+++` file header), stripped. Working-tree mode treats an
    untracked file as fully added. None = git could not produce the diff; the
    caller fails loud rather than reading it as "nothing was added"."""
    added: set[str] = set()

    def collect(args: list[str]) -> bool:
        r = subprocess.run(["git", *args, "--", rel], cwd=repo,
                           capture_output=True, text=True, errors="replace", check=False)
        if r.returncode != 0:
            return False
        for line in r.stdout.splitlines():
            if line.startswith("+") and not line.startswith("+++"):
                added.add(line[1:].strip())
        return True

    if staged_only:
        return added if collect(["diff", "--cached", "-U0"]) else None
    if since:
        return added if collect(["diff", "-U0", f"{since}..HEAD"]) else None
    # `git diff HEAD` already spans staged + unstaged changes
    if not collect(["diff", "-U0", "HEAD"]):
        return None
    r = subprocess.run(["git", "ls-files", "--others", "--exclude-standard",
                        "--", rel], cwd=repo, capture_output=True, text=True, errors="replace",
                       check=False)
    if r.returncode != 0:
        return None
    if r.stdout.strip():
        try:
            text = (repo / rel).read_text(encoding="utf-8", errors="replace")
        except OSError:
            return None  # untracked but unreadable: no evidence, fail loud upstream
        added.update(x.strip() for x in text.splitlines())
    return added


def _evidence_in_change(paths: list[str], repo: Path, staged_only: bool = False,
                        since: str | None = None) -> str | None:
    """Return a violation string when the change set lacks valid review evidence,
    else None. Evidence = an implemented ADR in THIS change set whose header zone
    carries a `Review: FULL/<date>/R1=ok R2=ok R3=ok` line ADDED by this change and
    not inherited from a note the same change set removes."""
    for rel in paths:
        if not rel.startswith(NOTES_DIR) or not rel.endswith(".md"):
            continue
        raw = _note_text(rel, repo, staged_only)
        if raw is None:
            continue
        head = _header_zone(raw)
        if "Status: implemented" not in "\n".join(head):
            continue  # proposed cannot self-certify
        title = next((ln.strip() for ln in head if ln.startswith("# Agent Note:")), "")
        added: set[str] | None = None
        inherited: tuple[set[tuple[str, str]], dict[str, set[str]]] | None = None
        for line in head:
            stripped = line.strip()
            m = REVIEW_LINE_RE.match(stripped)
            if not (m and _valid_date(m.group("date"))):
                continue
            if added is None:
                added = _added_lines_for(rel, repo, staged_only, since)
                if added is None:
                    return (f"cannot read the diff of {rel} (git failed) — the evidence "
                            "gate fails loud instead of guessing")
            if stripped not in added:
                continue  # not written by this change: no need to look further
            if inherited is None:
                inherited = _inherited_review_lines(repo, staged_only, since)
                if inherited is None:
                    return ("cannot determine which notes this change set removes "
                            "(git failed) — the evidence gate fails loud instead of "
                            "treating an unknown inherited set as empty")
            by_title, by_path = inherited
            if (title, stripped) in by_title or stripped in by_path.get(rel, ()):
                continue  # inherited from a note this batch removes or renames away
            return None
    return ("no implemented ADR in the change set carries a valid "
            "Review: FULL/<date>/R1=ok R2=ok R3=ok line added by this change "
            "(evidence must be newly produced by the batch; an existing Review: "
            "line carried along in the diff — including via a renamed or rewritten "
            "note — does not count)")


def _scan(repo: Path, staged_only: bool = False, since: str | None = None) -> list[str]:
    paths, err = _changed_paths_or_error(repo, staged_only, since)
    if err is not None:
        return ["cannot determine the change set: " + err +
                " — an undeterminable diff must not pass the review-tier gate"]
    if not paths:
        return []
    full, reasons = _classify(paths, repo)
    if not full:
        return []
    bad = _evidence_in_change(paths, repo, staged_only=staged_only, since=since)
    if bad is None:
        return []
    return [f"FULL-tier change lacks review evidence ({len(reasons)} trigger(s)): "
            + "; ".join(reasons) + f" | {bad}"]


def _new_repo(td: Path, name: str) -> Path:
    """Create an isolated fixture repo with a committed baseline."""
    repo = td / name
    (repo / NOTES_DIR / "implemented" / "process").mkdir(parents=True)
    (repo / "src" / "App").mkdir(parents=True)
    (repo / "src" / "App" / "Something.cs").write_text("// base", encoding="utf-8")

    def git(*args: str) -> None:
        subprocess.run(["git", *args], cwd=repo, capture_output=True, check=False)

    git("init", "-q")
    git("config", "user.email", "t@t")
    git("config", "user.name", "t")
    git("add", "-A")
    git("commit", "-qm", "init")
    return repo


def _write(repo: Path, rel: str, text: str) -> None:
    p = repo / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")
    subprocess.run(["git", "add", rel], cwd=repo, capture_output=True, check=False)


def _commit_all(repo: Path, msg: str) -> None:
    subprocess.run(["git", "add", "-A"], cwd=repo, capture_output=True, check=False)
    subprocess.run(["git", "commit", "-qm", msg], cwd=repo, capture_output=True, check=False)


def _self_test() -> int:
    """Offline fixtures, isolated per case, incl. the committed+clean-tree shape."""
    failed = 0

    def ok(cond: bool, msg: str) -> None:
        nonlocal failed
        if cond:
            print(f"  ok: {msg}")
        else:
            print(f"  \u2717 {msg}", file=sys.stderr)
            failed = 1

    EVIDENCE = ("# Agent Note: x\n\nStatus: implemented\n\n"
                "Review: FULL/2026-09-03/R1=ok R2=ok R3=ok\n\n"
                "## Problem\n\nx\n\n## Decision\n\nx\n\n"
                "## Alternatives considered\n\n- a\n\n## Consequences\n\nx\n")

    with tempfile.TemporaryDirectory() as td:
        # 1) LIGHT change passes without any evidence
        r = _new_repo(Path(td), "f1")
        _write(r, "src/App/Something.cs", "// changed (LIGHT)")
        ok(_scan(r) == [], "LIGHT change passes without evidence")

        # 2) FULL compose-root change is blocked without evidence
        r = _new_repo(Path(td), "f2")
        _write(r, "src/App/DesktopBootstrap.cs", "// x")
        rows = _scan(r)
        ok(any("FULL-tier change lacks review evidence" in x for x in rows),
           "FULL compose-root change blocked without evidence")

        # 3) FULL compose-root passes when the SAME change carries an implemented
        #    ADR with a valid Review line
        r = _new_repo(Path(td), "f3")
        _write(r, "src/App/DesktopBootstrap.cs", "// x")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md", EVIDENCE)
        ok(_scan(r) == [], "FULL change passes when change carries Review evidence")

        # 4) gate-criteria change (ArchitectureTests) classifies FULL, blocked w/o evidence
        r = _new_repo(Path(td), "f4")
        _write(r, "tests/App/ArchitectureTests.cs", "// x")
        rows = _scan(r)
        ok(any("gate-criteria" in x for x in rows),
           "gate-criteria change classifies FULL (ArchitectureTests)")

        # 5) proposed ADR promising full review classifies FULL, blocked w/o evidence
        r = _new_repo(Path(td), "f5")
        _write(r, NOTES_DIR + "/proposed/architecture/2026-09-03-y.md",
               "# Agent Note: y\n\nStatus: proposed\n\n三重审核（R1/R2/R3）确认零行为变更\n")
        rows = _scan(r)
        ok(any("adr-promises-full" in x for x in rows),
           "proposed ADR promising 三重审核 classifies FULL")

        # 6) committed + clean tree: --since must catch an outgoing FULL change
        r = _new_repo(Path(td), "f6")
        _commit_all(r, "base")
        _write(r, "src/App/DesktopBootstrap.cs", "// new FULL change")
        _commit_all(r, "full change w/o evidence")
        rows = _scan(r, since="HEAD~1")
        ok(any("FULL-tier change lacks review evidence" in x for x in rows),
           "--since catches an outgoing FULL change on a clean tree")

        # 7) committed + clean tree, WITH evidence ADR in the same range: passes
        r = _new_repo(Path(td), "f7")
        _commit_all(r, "base")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md", EVIDENCE)
        _write(r, "src/App/DesktopBootstrap.cs", "// new FULL change")
        _commit_all(r, "full change w/ evidence")
        rows = _scan(r, since="HEAD~1")
        ok(rows == [], "--since passes when the range carries Review evidence")

        # 8) stale evidence re-armed by a touch does NOT clear (B2): the evidence
        #    ADR was committed earlier; a later FULL change touches it one line.
        #    Its Review: line is not an added line of this batch, so it is inert.
        r = _new_repo(Path(td), "f8")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md", EVIDENCE)
        _commit_all(r, "evidence first")
        # touch the evidence ADR (a comment) together with a brand-new FULL change
        (r / NOTES_DIR / "implemented" / "process" / "2026-09-03-x.md").write_text(
            EVIDENCE + "\n<!-- touched later -->\n", encoding="utf-8")
        _write(r, "src/App/DesktopBootstrap.cs", "// brand new")
        _commit_all(r, "touch evidence + new FULL change")
        rows = _scan(r, since="HEAD~1")
        ok(any("FULL-tier change lacks review evidence" in x for x in rows),
           "--since blocks a touch-only re-armed stale evidence ADR")

        # 9) review values strictly checked: R1=fail does NOT count as evidence
        r = _new_repo(Path(td), "f9")
        _write(r, "src/App/DesktopBootstrap.cs", "// x")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md",
               EVIDENCE.replace("R1=ok", "R1=fail"))
        rows = _scan(r)
        ok(any("FULL-tier change lacks review evidence" in x for x in rows),
           "R1=fail does not count as review evidence")

        # 10) proposed ADR self-adding a Review line does NOT clear (must be implemented)
        r = _new_repo(Path(td), "f10")
        _write(r, "src/App/DesktopBootstrap.cs", "// x")
        _write(r, NOTES_DIR + "/proposed/architecture/2026-09-03-y.md",
               "# Agent Note: y\n\nStatus: proposed\n\nReview: FULL/2026-09-03/R1=ok R2=ok R3=ok\n")
        rows = _scan(r)
        ok(any("FULL-tier change lacks review evidence" in x for x in rows),
           "proposed ADR self-Review does not clear a FULL change")

        # 11) freshly produced evidence clears even when an old ADR is touched in
        #     the same batch (the freshness rule narrows the filler, not the batch)
        r = _new_repo(Path(td), "f11")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md", EVIDENCE)
        _commit_all(r, "evidence first")
        (r / NOTES_DIR / "implemented" / "process" / "2026-09-03-x.md").write_text(
            EVIDENCE + "\n<!-- touched later -->\n", encoding="utf-8")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-13-y.md", EVIDENCE)
        _write(r, "src/App/DesktopBootstrap.cs", "// brand new")
        _commit_all(r, "touch old evidence + new evidence + FULL change")
        rows = _scan(r, since="HEAD~1")
        ok(rows == [],
           "newly produced evidence clears a FULL batch that also touches an old ADR")

        # 12) an inherited Review: line must not travel by rename: whether or not
        #     git pairs the notes, the line was not ADDED by this change
        r = _new_repo(Path(td), "f12")
        header = ("# Agent Note: x\n\nStatus: implemented\n\n"
                  "Review: FULL/2026-09-03/R1=ok R2=ok R3=ok\n\n")
        body_old = "\n".join(f"old body line {i}" for i in range(40))
        body_new = "\n".join(f"fully rewritten body line {i}" for i in range(40))
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md",
               header + "## Problem\n\n" + body_old +
               "\n\n## Alternatives considered\n\n- a\n\n## Consequences\n\n" + body_old + "\n")
        _commit_all(r, "old evidence")
        old = r / NOTES_DIR / "implemented" / "process" / "2026-09-03-x.md"
        (r / NOTES_DIR / "implemented" / "process" / "2026-09-13-renamed.md").write_text(
            header + "## Problem\n\n" + body_new +
            "\n\n## Alternatives considered\n\n- b\n\n## Consequences\n\n" + body_new + "\n",
            encoding="utf-8")
        old.unlink()
        _write(r, "src/App/DesktopBootstrap.cs", "// brand new")
        _commit_all(r, "rename+rewrite evidence + FULL change")
        rows = _scan(r, since="HEAD~1")
        ok(any("FULL-tier change lacks review evidence" in x for x in rows),
           "a Review: line inherited via rename+rewrite does not count as evidence")

        # 13) an unresolvable --since base must fail loud, never pass as empty
        r = _new_repo(Path(td), "f13")
        rows = _scan(r, since="nosuchref")
        ok(any("cannot determine the change set" in x for x in rows),
           "an unreachable --since base is a violation (no silent green)")

        # 14) --staged reads the note from the index: staging evidence and then
        #     deleting the working-tree copy (a partial-commit flow) still clears
        r = _new_repo(Path(td), "f14")
        _commit_all(r, "base")
        _write(r, "src/App/DesktopBootstrap.cs", "// new FULL change")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md", EVIDENCE)
        (r / NOTES_DIR / "implemented" / "process" / "2026-09-03-x.md").unlink()
        ok(_scan(r, staged_only=True) == [],
           "--staged clears when the staged index carries the evidence ADR")

        # 15) default working-tree mode: an ADR written but not `git add`-ed is
        #     untracked, so it counts as fully added (ad-hoc local check)
        r = _new_repo(Path(td), "f15")
        _commit_all(r, "base")
        (r / "src" / "App" / "DesktopBootstrap.cs").write_text("// new FULL change",
                                                               encoding="utf-8")
        note = r / NOTES_DIR / "implemented" / "process" / "2026-09-03-x.md"
        note.parent.mkdir(parents=True, exist_ok=True)
        note.write_text(EVIDENCE, encoding="utf-8")
        ok(_scan(r) == [],
           "an untracked ADR counts as fully added in working-tree mode")

        # 16) .github/workflows/** is a behavior-surface FULL trigger
        r = _new_repo(Path(td), "f16")
        _write(r, ".github/workflows/ci.yml", "name: ci\n")
        rows = _scan(r)
        ok(any("behavior-surface: .github/workflows/ci.yml" in x for x in rows),
           "a workflow change classifies FULL (behavior-surface)")

        # 17) a detected rename whose title the batch also changed: the Review:
        #     line travels with the same note, so the destination path keys it
        r = _new_repo(Path(td), "f17")
        header_alpha = ("# Agent Note: alpha\n\nStatus: implemented\n\n"
                        "Review: FULL/2026-09-03/R1=ok R2=ok R3=ok\n\n")
        body = "\n".join(f"body line {i}" for i in range(40))
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-alpha.md",
               header_alpha + "## Problem\n\n" + body +
               "\n\n## Alternatives considered\n\n- a\n\n## Consequences\n\n" + body + "\n")
        _commit_all(r, "old evidence")
        (r / NOTES_DIR / "implemented" / "process" / "2026-09-03-alpha.md").unlink()
        _write(r, NOTES_DIR + "/implemented/process/2026-09-13-beta.md",
               header_alpha.replace("alpha", "beta") + "## Problem\n\n" + body +
               "\n\n## Alternatives considered\n\n- a\n\n## Consequences\n\n" + body + "\n")
        _write(r, "src/App/DesktopBootstrap.cs", "// brand new")
        _commit_all(r, "rename + retitle evidence + FULL change")
        rows = _scan(r, since="HEAD~1")
        ok(any("FULL-tier change lacks review evidence" in x for x in rows),
           "a retitled rename does not launder the inherited Review: line")

        # 18) the other direction: at a rename destination a FRESH Review: line
        #     (different text) is newly produced evidence and clears — the
        #     exclusion is keyed on the line text, not on the destination path
        r = _new_repo(Path(td), "f18")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-alpha.md",
               header_alpha + "## Problem\n\n" + body +
               "\n\n## Alternatives considered\n\n- a\n\n## Consequences\n\n" + body + "\n")
        _commit_all(r, "old evidence")
        (r / NOTES_DIR / "implemented" / "process" / "2026-09-03-alpha.md").unlink()
        _write(r, NOTES_DIR + "/implemented/process/2026-09-13-beta.md",
               header_alpha.replace("alpha", "beta").replace("2026-09-03/R1", "2026-09-14/R1")
               + "## Problem\n\n" + body +
               "\n\n## Alternatives considered\n\n- a\n\n## Consequences\n\n" + body + "\n")
        _write(r, "src/App/DesktopBootstrap.cs", "// brand new")
        _commit_all(r, "rename + fresh evidence + FULL change")
        ok(_scan(r, since="HEAD~1") == [],
           "a rename destination carrying a fresh Review: line clears")

    if failed == 0:
        print("== verify-review-tier self-test passed ==")
    else:
        print("== verify-review-tier self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(
        description="Verify review-tier classification and evidence gate")
    parser.add_argument("--repo", default=".", help="repo root (default cwd)")
    parser.add_argument("--staged", action="store_true",
                        help="scan only the index vs HEAD (pre-commit use)")
    parser.add_argument("--since", default=None, metavar="BASE",
                        help="scan committed range BASE..HEAD (pre-push/CI use)")
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 when a FULL-tier diff lacks review evidence")
    args = parser.parse_args()
    if args.staged and args.since:
        print("error: --staged and --since are mutually exclusive", file=sys.stderr)
        return 2

    repo = Path(args.repo).resolve()
    rows = _scan(repo, staged_only=args.staged, since=args.since)
    if rows:
        print(f"review-tier: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        if args.enforce:
            return 1
    else:
        print("review-tier: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
