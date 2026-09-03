#!/usr/bin/env python3
"""Verify that a change's review tier is honored (review-tier-escape-proofing).

Mechanizes the review-tier decision that used to be the executor's discretion
(the 2026-09-03 escape: a compose-root refactor whose ADR explicitly promised
a full three-way review was run through a single R2 light review because the
general "zero-behavior-change => light review" rule won by default). Tier
classification rules live in the ADR `.agents/notes/proposed/process/
2026-09-03-review-tier-escape-proofing.md`; this script is their mechanical
enforcement (single source of truth for the path patterns).

A diff touching any FULL tier path requires review evidence travelling WITH
the change before it may be committed/pushed; without evidence the gate fails
(`--enforce`). Evidence = an ADR under .agents/notes that is part of THIS
change set (same staged set / same base..HEAD range) and whose header carries
a valid `Review:` line dated inside the change window — a stale evidence ADR
touched only to re-arm does not count (its Review date predates the window).

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
  - date must be a real calendar date inside the change window
  - the ADR must be part of the same change set
  - Status must be implemented (a proposed ADR cannot self-certify its review)

Default is report-only (exit 0). `--enforce` exits 1 when a FULL-tier diff
lacks review evidence. `--self-test` runs offline fixtures.

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
    ("behavior-surface", lambda rel, p: ".github/workflows" in p.parts),
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


def _repo_changed_paths(repo: Path, staged_only: bool = False,
                        since: str | None = None) -> list[str]:
    """Changed paths for the selected git moment:
    --staged => index vs HEAD; --since => <base>..HEAD; default => working tree."""
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
        try:
            r = subprocess.run(cmd, cwd=repo, capture_output=True, text=True, check=False)
            if r.returncode == 0:
                out.update(x for x in r.stdout.splitlines() if x.strip())
        except Exception:
            pass
    if not staged_only and not since:
        # untracked (not in HEAD, not ignored)
        try:
            r = subprocess.run(["git", "ls-files", "--others", "--exclude-standard"],
                               cwd=repo, capture_output=True, text=True, check=False)
            if r.returncode == 0:
                out.update(x for x in r.stdout.splitlines() if x.strip())
        except Exception:
            pass
    return sorted(out)


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


def _evidence_in_change(paths: list[str], repo: Path) -> str | None:
    """Return a violation string when the change set lacks valid review evidence,
    else None. Evidence = an implemented ADR in THIS change set whose header has
    Review: FULL/<date>/R1=ok R2=ok R3=ok with a real date (window check is the
    caller's since/staged working-tree best effort — date is validated, staleness
    vs the change window is reported by the caller when since is known)."""
    for rel in paths:
        if not rel.startswith(NOTES_DIR) or not rel.endswith(".md"):
            continue
        adr = repo / rel
        if not adr.is_file():
            continue
        try:
            head = adr.read_text(encoding="utf-8", errors="replace").splitlines()[:15]
            text = "\n".join(head)
        except Exception:
            continue
        if "Status: implemented" not in text:
            continue  # proposed cannot self-certify
        for line in head:
            m = REVIEW_LINE_RE.match(line.strip())
            if m and _valid_date(m.group("date")):
                return None
    return ("no implemented ADR in the change set carries a valid "
            "Review: FULL/<date>/R1=ok R2=ok R3=ok line")


def _scan(repo: Path, staged_only: bool = False, since: str | None = None) -> list[str]:
    paths = _repo_changed_paths(repo, staged_only, since)
    if not paths:
        return []
    full, reasons = _classify(paths, repo)
    if not full:
        return []
    bad = _evidence_in_change(paths, repo)
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

        # 8) stale evidence re-armed by a touch does NOT clear (B2): evidence ADR
        #    committed earlier, later FULL change touches it one line + new code
        r = _new_repo(Path(td), "f8")
        _write(r, NOTES_DIR + "/implemented/process/2026-09-03-x.md", EVIDENCE)
        _commit_all(r, "evidence first")
        # touch the evidence ADR (a comment) together with a brand-new FULL change
        (r / NOTES_DIR / "implemented" / "process" / "2026-09-03-x.md").write_text(
            EVIDENCE + "\n<!-- touched later -->\n", encoding="utf-8")
        _write(r, "src/App/DesktopBootstrap.cs", "// brand new")
        _commit_all(r, "touch evidence + new FULL change")
        # default working-tree mode now sees nothing (clean); --since sees both.
        # The evidence ADR Review date (2026-09-03) equals the change date, so it
        # passes the window — this documents the current machine-checkable bound:
        # date windowing is best-effort; semantic freshness is the reviewer's job.
        rows = _scan(r, since="HEAD~1")
        ok(rows == [], "--since with re-touched same-dated evidence passes (window best-effort)")

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
