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

Usage: python3 verify-md-links.py [root_dir] [--include-skills]
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import sys
from pathlib import Path

LINK_RE = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
EXCLUDED_DIR_PARTS = (".dotnet-cache", ".cache", ".noogenesis", "resources", "bin", "obj", "node_modules")
HEADING_RE = re.compile(r"^(#{1,6})\s+(.+?)\s*#*\s*$")
ANCHOR_RE = re.compile(r'<a\s+id="([^"]+)"')


def slugify(text: str) -> str:
    text = text.strip().lower()
    text = re.sub(r"[^\w\u4e00-\u9fff \-]", "", text)
    text = re.sub(r"\s+", "-", text)
    return text


def heading_slugs(path: Path) -> set[str]:
    slugs: set[str] = set()
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return slugs
    for line in lines:
        m = HEADING_RE.match(line)
        if m:
            slugs.add(slugify(m.group(2)))
        m = ANCHOR_RE.search(line)
        if m:
            slugs.add(m.group(1))
    return slugs


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("root", nargs="?", default=".")
    ap.add_argument("--include-skills", action="store_true",
                    help="also check skills/ (vendored sources, upstream refs)")
    args = ap.parse_args()
    root = Path(args.root)
    errors: list[str] = []
    checked = 0
    for md in sorted(root.rglob("*.md")):
        if not args.include_skills and "skills" in md.parts:
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

    print(f"Checked {checked} link targets")
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())