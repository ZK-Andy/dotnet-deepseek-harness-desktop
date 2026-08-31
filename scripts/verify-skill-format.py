#!/usr/bin/env python3
"""Verify project skill format: frontmatter, directory bundle, links, structure.

Checks every `<dir>/SKILL.md` under `.agents/skills/`:
  - frontmatter `name` exists and is lowercase-kebab (`^[a-z0-9]+(-[a-z0-9]+)*$`)
  - frontmatter `description` exists and is non-empty
  - the directory is a `<name>/SKILL.md` bundle (dir basename == name)
  - no root-level README.md inside `.agents/skills/` (DSH parses it as a flat skill)
  - relative .md links resolve to an existing file and, if they carry a `#frag`,
    the fragment matches a heading slug or explicit `<a id="...">`
  - body contains a positioning line and a workflow section (guards empty filler)

Skills are the project's own; references must resolve against this repo (this
script intentionally does NOT skip skills/, unlike verify-md-links, because the
whole point is to surface broken references that upstream skills would hide).

Usage: python3 scripts/verify-skill-format.py [--self-test]
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SKILLS_DIR = ROOT / ".agents" / "skills"

NAME_RE = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
FRONTMATTER_RE = re.compile(r"^---\s*$")
LINK_RE = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
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


def parse_frontmatter(text: str) -> dict | None:
    """Return the frontmatter dict, or None if no frontmatter block at top."""
    lines = text.splitlines()
    if not lines or not FRONTMATTER_RE.match(lines[0]):
        return None
    end = None
    for i in range(1, len(lines)):
        if FRONTMATTER_RE.match(lines[i]):
            end = i
            break
    if end is None:
        return None
    fm: dict[str, str] = {}
    for line in lines[1:end]:
        if ":" in line:
            k, _, v = line.partition(":")
            fm[k.strip()] = v.strip().strip('"').strip("'")
    return fm


def check_skill(path: Path, errors: list[str]) -> None:
    text = path.read_text(encoding="utf-8")
    fm = parse_frontmatter(text)
    if fm is None:
        errors.append(f"{path}: missing YAML frontmatter block")
        return

    name = fm.get("name", "")
    if not name:
        errors.append(f"{path}: frontmatter 'name' missing")
    elif not NAME_RE.match(name):
        errors.append(f"{path}: name '{name}' not lowercase-kebab")

    desc = fm.get("description", "")
    if not desc:
        errors.append(f"{path}: frontmatter 'description' missing/empty")

    # directory bundle: dir basename == name
    if name:
        dirname = path.parent.name
        if dirname != name:
            errors.append(
                f"{path}: bundle dir '{dirname}' != frontmatter name '{name}'"
            )

    # relative md links resolve
    for target in LINK_RE.findall(text):
        target = target.strip()
        if target.startswith(("http://", "https://", "mailto:", "#", "<")):
            continue
        if "://" in target:
            continue
        if target.startswith("/"):
            resolved = (ROOT / target.lstrip("/")).resolve()
        else:
            resolved = (path.parent / target.split("#")[0]).resolve()
        if not resolved.is_file():
            errors.append(f"{path}: missing target '{target}'")
            continue
        if "#" in target:
            frag = target.split("#", 1)[1]
            if frag and frag not in heading_slugs(resolved):
                errors.append(f"{path}: dead anchor '#{frag}' in '{target}'")

    # structure guards: positioning + workflow
    body = text
    if not re.search(r"guidance, not a script", body, re.IGNORECASE) and \
       not re.search(r"不是脚本|是引导", body):
        errors.append(f"{path}: missing 'guidance, not a script' positioning line")
    if not re.search(r"^## .*Workflow|^## .*工作流|^## .*工作流程", body, re.MULTILINE):
        errors.append(f"{path}: missing '## Workflow' section")


def scan(errors: list[str]) -> int:
    if not SKILLS_DIR.is_dir():
        errors.append(f"{SKILLS_DIR}: skills dir missing")
        return 1
    # no root-level README.md in skills/ (DSH parses flat .md)
    for md in SKILLS_DIR.glob("*.md"):
        errors.append(f"{SKILLS_DIR / md.name}: root-level README/`.md` parsed as flat skill")
    skills = sorted(SKILLS_DIR.glob("*/SKILL.md"))
    for skill in skills:
        check_skill(skill, errors)
    return len(skills)


def self_test() -> int:
    """Real coverage: run check_skill against both a compliant and a violating
    sample, and run scan() against the real skills dir, so the self-test is not
    a set of isolated assertions that never touch the main logic."""
    import tempfile

    # --- frontmatter/name regex unit checks ---
    assert parse_frontmatter("---\nname: dsh-foo\n---\n# x\n") is not None
    assert parse_frontmatter("# no frontmatter\n") is None
    assert NAME_RE.match("dsh-foo")
    assert not NAME_RE.match("Dsh-Foo")
    assert not NAME_RE.match("dsh_foo")

    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        ok_dir = td / "ok-skill"
        ok_dir.mkdir()
        ok = ok_dir / "SKILL.md"
        ok.write_text(
            "---\nname: ok-skill\ndescription: Use when testing.\n---\n\n"
            "# Title\n\n**This skill is guidance, not a script.**\n\n"
            "## Workflow\n\n1. Do the thing.\n",
            encoding="utf-8",
        )
        errs: list[str] = []
        check_skill(ok, errs)
        assert not errs, f"compliant sample should pass, got {errs}"

        bad1 = td / "Bad_Skill"
        bad1.mkdir()
        bad1file = bad1 / "SKILL.md"
        bad1file.write_text("---\nname: Bad_Skill\n---\n\n# Title\n\nNo workflow here.\n",
                            encoding="utf-8")
        errs = []
        check_skill(bad1file, errs)
        joined = "\n".join(errs)
        assert "not lowercase-kebab" in joined, f"should flag bad name: {errs}"
        assert "missing '## Workflow'" in joined, f"should flag missing workflow: {errs}"

        bad2 = td / "dead-link"
        bad2.mkdir()
        bad2file = bad2 / "SKILL.md"
        bad2file.write_text(
            "---\nname: dead-link\ndescription: Use when testing.\n---\n\n"
            "# Title\n\n**This skill is guidance, not a script.**\n\n"
            "## Workflow\n\nSee [missing](../../nope.md).\n", encoding="utf-8")
        errs = []
        check_skill(bad2file, errs)
        assert any("missing target" in e for e in errs), f"should flag dead link: {errs}"

    # --- real-scan smoke: the actual skills dir must be green ---
    errs = []
    n = scan(errs)
    assert n >= 8, f"expected >=8 skills, got {n}"
    print(f"verify-skill-format --self-test OK (frontmatter/dir-bundle/link/structure; scan={n} real skills)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    errors: list[str] = []
    n = scan(errors)
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        print(f"{n} skills, {len(errors)} violations")
        return 1
    print(f"OK: {n} skills conform")
    return 0


if __name__ == "__main__":
    sys.exit(main())
