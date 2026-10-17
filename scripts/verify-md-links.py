#!/usr/bin/env python3
"""Verify relative Markdown links: file targets exist and #fragment anchors resolve.

Checks, for every .md file under the given root (default: current directory):
  - `](relative/path.md)`  -> target file must exist
  - `](relative/path.md#slug)` -> target exists AND slug must match a heading
    slug (GitHub-style: lowercase, spaces->hyphens, strip punctuation) or an
    explicit <a id="slug"> anchor in that file
  - `](https://…)` / `](mailto:…)` / `](<…>)` -> skipped (external)
  - only `](…)` link targets are checked; a target resolves relative to the
    containing file, and a `/`-prefixed target against the scanned root (the
    repository root)

The exclusion set has three classes (this docstring is their single source of
truth; the code constant EXCLUDED_DIR_PARTS below is its executable form):
  - `skills/` directories: vendored skill sources keep their upstream path
    references, which only resolve after path mapping. Pass --include-skills
    to check them anyway.
  - `.plan/` (incl. journal): gitignored local-only narrative; its relative
    resolution base differs from committed docs, so per discipline its links
    are not validated.
  - third-party / generated trees (`.dotnet-cache`, `.cache`, `.noogenesis`,
    `resources`, `bin`, `obj`, `node_modules`): vendored copies and build
    outputs carry foreign repo-relative links that never resolve here.
Archived notes (`.agents/notes/archived/`) are NOT excluded: content is frozen,
but their relative links are machine-checked — the E-batch doc/ADR cleanup
repaired the historical dead links and narrowed this exclusion, so frozen
content with silently dead pointers is a gap this gate now covers
(ADR `process/2026-09-28-doc-adr-layer-cleanup`).

Tracking rule (rationale home: ADR `process/2026-10-18-md-links-tracked-targets`):
  - only git-tracked documents are validated; local-only notes (`HANDOFF*.md`,
    `.evolve/`, …) are not part of any clone, so their own links are out of scope;
  - a link target must be git-tracked AND inside the repository — a local-only
    or out-of-repo target is dead for every reader of a clone;
  - `git ls-files` supplies the tracked set; if it cannot be read the gate fails
    loud (with git's own stderr) instead of silently dropping the rule.
Boundary: the tracked set is `git ls-files`, i.e. the index — a brand-new
document joins it at `git add`, so running the gate before staging skips that
document. The hooks (`pre-commit`, `pre-push`) invoke this gate on the whole
work tree; the index supplies the tracked set, which is why a staged new
document is covered there.

The slug/anchor/link-syntax model lives in gate_common (one home, shared with
verify-skill-format; ADR `process/2026-09-29-gate-common-shared-module`).

Usage: python3 verify-md-links.py [root_dir] [--include-skills] [--self-test]
Exit code 0 = pass (or self-test passed), 1 = violations (or self-test failed).
"""

import argparse
import subprocess
import sys
import tempfile
from pathlib import Path

from gate_common import LINK_RE, SelfTest, heading_slugs

EXCLUDED_DIR_PARTS = (".dotnet-cache", ".cache", ".noogenesis", "resources", "bin", "obj", "node_modules")


def _rel(path: Path, root_resolved: Path) -> str | None:
    """Repository-relative posix path, or None when `path` lies outside the root."""
    try:
        return path.resolve().relative_to(root_resolved).as_posix()
    except ValueError:
        return None


def tracked_paths(root: Path) -> tuple[set[str] | None, str]:
    """(git-tracked paths relative to `root`, why it failed).

    `git ls-files` prints paths relative to the directory it runs in, which is
    exactly the base `check_tree` resolves against. The second element carries
    git's own stderr so a failure (`safe.directory`, missing git, not a work
    tree) is diagnosable instead of collapsing into one guess."""
    try:
        out = subprocess.run(
            ["git", "-C", str(root), "ls-files", "-z"],
            capture_output=True, check=True, text=True)
    except (OSError, subprocess.CalledProcessError) as ex:
        detail = (getattr(ex, "stderr", "") or str(ex) or "").strip()
        return None, detail
    return {p for p in out.stdout.split("\0") if p}, ""


def check_tree(root: Path, include_skills: bool, tracked: set[str]) -> tuple[list[str], int, int, int]:
    """Walk `root` and return (error lines, resolved targets, docs scanned, local-only docs skipped)."""
    errors: list[str] = []
    checked = 0
    scanned = 0
    skipped = 0
    root_resolved = root.resolve()
    for md in sorted(root.rglob("*.md")):
        if not include_skills and "skills" in md.parts:
            continue
        if any(seg in md.parts for seg in EXCLUDED_DIR_PARTS):
            continue
        # .plan/ 排除的 rationale 唯一家在本 docstring
        if ".plan" in md.parts:
            continue
        # 归档笔记不排除（排除面的唯一家是本 docstring）：内容冻结，但相对链接自 E 批
        # 文档/ADR 清理起由本门禁校验（历史死链已修）；冻结指正文与决定不再改写，
        # 指针移动类纯链接修复除外（见 .agents/notes/README.md 归档纪律）。
        if _rel(md, root_resolved) not in tracked:
            skipped += 1  # 本地专有文档：不进克隆，其自身链接不在本门禁的读者面内
            continue
        scanned += 1
        text = md.read_text(encoding="utf-8")
        for target in LINK_RE.findall(text):
            target = target.strip()
            if target.startswith(("http://", "https://", "mailto:", "#", "<")):
                continue
            if "://" in target:
                continue
            path_part = target.split("#")[0]
            if target.startswith("/"):  # repo-root absolute: resolve against root
                resolved = (root / path_part.lstrip("/")).resolve()
            else:
                resolved = (md.parent / path_part).resolve()
            if not resolved.is_file():
                errors.append(f"{md}: missing target '{target}'")
                continue
            rel = _rel(resolved, root_resolved)
            if rel is None:
                errors.append(f"{md}: target '{target}' resolves outside the repository "
                              f"(dead link in a clone)")
                continue
            if rel not in tracked:
                errors.append(f"{md}: target '{target}' is not tracked by git "
                              f"(dead link in a clone)")
                continue
            checked += 1
            if "#" in target:
                frag = target.split("#", 1)[1]
                if frag and frag not in heading_slugs(resolved):
                    errors.append(f"{md}: dead anchor '#{frag}' in '{target}'")
    return errors, checked, scanned, skipped


def _self_test() -> int:
    st = SelfTest()
    with tempfile.TemporaryDirectory() as td:
        base = Path(td)
        root = base / "repo"
        root.mkdir()
        (base / "outside.md").write_text("# outside\n", encoding="utf-8")
        (root / "target.md").write_text(
            "# T\n\n## Head One\n\n<a id=\"custom\"></a>\n", encoding="utf-8")
        (root / "good.md").write_text(
            "[file](target.md)\n"
            "[heading](target.md#head-one)\n"
            "[anchor](target.md#custom)\n"
            "[ext](https://example.com/x)\n"
            "[mail](mailto:a@b.c)\n"
            "[angle](<target.md>)\n", encoding="utf-8")
        (root / "broken.md").write_text(
            "[missing](nope.md)\n[dead](target.md#no-such-anchor)\n", encoding="utf-8")
        # target exists on disk but is not tracked (gitignored local doc)
        (root / "local-only.md").write_text("# local\n", encoding="utf-8")
        (root / "local.md").write_text("[local](local-only.md)\n", encoding="utf-8")
        # target inside the repo but outside its root on disk
        (root / "escape.md").write_text("[out](../outside.md)\n", encoding="utf-8")
        # untracked source document: its own links must not be validated
        (root / "ignored-note.md").write_text("[missing](nope.md)\n", encoding="utf-8")

        # exclusion classes: tracked on purpose, so only the exclusion rules can
        # keep their deliberately broken links quiet
        plan = root / ".plan"
        plan.mkdir()
        (plan / "note.md").write_text("[missing](nope.md)\n", encoding="utf-8")
        vendored = root / "bin"
        vendored.mkdir()
        (vendored / "gen.md").write_text("[missing](nope.md)\n", encoding="utf-8")
        skills = root / "skills" / "some-skill"
        skills.mkdir(parents=True)
        (skills / "SKILL.md").write_text("[missing](nope.md)\n", encoding="utf-8")

        # 合成入库集：local-only.md / ignored-note.md 故意缺席（模拟 gitignore 的本地文件）
        tracked = {
            "target.md", "good.md", "broken.md", "local.md", "escape.md",
            ".plan/note.md", "bin/gen.md", "skills/some-skill/SKILL.md",
        }
        errors, checked, scanned, skipped = check_tree(root, include_skills=False, tracked=tracked)
        st.ok(errors == [
            f"{root / 'broken.md'}: missing target 'nope.md'",
            f"{root / 'broken.md'}: dead anchor '#no-such-anchor' in 'target.md#no-such-anchor'",
            f"{root / 'escape.md'}: target '../outside.md' resolves outside the repository "
            f"(dead link in a clone)",
            f"{root / 'local.md'}: target 'local-only.md' is not tracked by git "
            f"(dead link in a clone)",
        ], f"broken/untracked/outside targets flagged, exclusions + externals skipped: {errors}")
        st.ok(checked == 4,
              f"four targets resolved (a dead anchor's file still counts), got {checked}")
        st.ok((scanned, skipped) == (5, 2),
              f"five tracked docs scanned, two local-only docs skipped, got {(scanned, skipped)}")

        errs_skills, _, _, _ = check_tree(root, include_skills=True, tracked=tracked)
        st.ok(any("skills" in e for e in errs_skills),
              "--include-skills lifts the skills/ exclusion")

    return st.finish("verify-md-links")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("root", nargs="?", default=".")
    ap.add_argument("--include-skills", action="store_true",
                    help="also check skills/ (vendored sources, upstream refs)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return _self_test()
    root = Path(args.root)
    tracked, why = tracked_paths(root)
    if tracked is None:
        # fail loud rather than silently dropping the rule: a gate that quietly
        # forgets half its judgement is indistinguishable from a green gate.
        print(f"FAIL: cannot read the git-tracked file set under {root}: "
              f"{why or 'git ls-files failed'} — tracking rule cannot be enforced")
        return 1
    errors, checked, scanned, skipped = check_tree(root, args.include_skills, tracked)

    print(f"Checked {checked} link targets "
          f"({scanned} tracked docs scanned, {skipped} local-only docs skipped)")
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
