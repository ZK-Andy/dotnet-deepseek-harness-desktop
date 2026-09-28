#!/usr/bin/env python3
"""Verify relative Markdown links: file targets exist and #fragment anchors resolve.

Checks, for every .md file under the given root (default: current directory):
  - `](relative/path.md)`  -> target file must exist
  - `](relative/path.md#slug)` -> target exists AND slug must match a heading
    slug (GitHub-style: lowercase, spaces->hyphens, strip punctuation) or an
    explicit <a id="slug"> anchor in that file
  - `](https://…)` / `](mailto:…)` / `](<…>)` -> skipped (external)
  - bare filenames or absolute paths are NOT validated here

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
but their relative links are machine-checked — since the E-batch doc/ADR
cleanup repaired the historical dead links, frozen content with silently dead
pointers is a gap this gate now covers (ADR post-packaging-churn-restructure).

The slug/anchor/link-syntax model lives in gate_common (one home, shared with
verify-skill-format; ADR `process/2026-09-29-gate-common-shared-module`).

Usage: python3 verify-md-links.py [root_dir] [--include-skills] [--self-test]
Exit code 0 = pass (or self-test passed), 1 = violations (or self-test failed).
"""

import argparse
import sys
import tempfile
from pathlib import Path

from gate_common import LINK_RE, SelfTest, heading_slugs

EXCLUDED_DIR_PARTS = (".dotnet-cache", ".cache", ".noogenesis", "resources", "bin", "obj", "node_modules")


def check_tree(root: Path, include_skills: bool) -> tuple[list[str], int]:
    """Walk `root` and return (error lines, count of resolved link targets)."""
    errors: list[str] = []
    checked = 0
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
        text = md.read_text(encoding="utf-8")
        for target in LINK_RE.findall(text):
            target = target.strip()
            if target.startswith(("http://", "https://", "mailto:", "#", "<")):
                continue
            if "://" in target:
                continue
            if target.startswith("/"):  # repo-root absolute: resolve against root
                resolved = (root / target.lstrip("/")).resolve()
            else:
                resolved = (md.parent / target.split("#")[0]).resolve()
            if not resolved.is_file():
                errors.append(f"{md}: missing target '{target}'")
                continue
            checked += 1
            if "#" in target:
                frag = target.split("#", 1)[1]
                if frag and frag not in heading_slugs(resolved):
                    errors.append(f"{md}: dead anchor '#{frag}' in '{target}'")
    return errors, checked


def _self_test() -> int:
    st = SelfTest()
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
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

        # exclusion classes: files under these parts are never scanned
        plan = root / ".plan"
        plan.mkdir()
        (plan / "note.md").write_text("[missing](nope.md)\n", encoding="utf-8")
        vendored = root / "bin"
        vendored.mkdir()
        (vendored / "gen.md").write_text("[missing](nope.md)\n", encoding="utf-8")
        skills = root / "skills" / "some-skill"
        skills.mkdir(parents=True)
        (skills / "SKILL.md").write_text("[missing](nope.md)\n", encoding="utf-8")

        errors, checked = check_tree(root, include_skills=False)
        st.ok(errors == [
            f"{root / 'broken.md'}: missing target 'nope.md'",
            f"{root / 'broken.md'}: dead anchor '#no-such-anchor' in 'target.md#no-such-anchor'",
        ], f"broken file flagged, exclusions + externals skipped: {errors}")
        st.ok(checked == 4,
              f"four targets resolved (a dead anchor's file still counts), got {checked}")

        errs_skills, _ = check_tree(root, include_skills=True)
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
    errors, checked = check_tree(Path(args.root), args.include_skills)

    print(f"Checked {checked} link targets")
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
