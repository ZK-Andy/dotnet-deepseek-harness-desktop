#!/usr/bin/env python3
"""Verify review briefs exist and are well-formed (review-scope-narrowing 4.1).

Every review lane (R1/R2/R3) must be launched from a brief written by the main
session BEFORE the review agent starts — the brief is what bounds the review
task (scope + directed checks + explicit out-of-scope) so the agent can finish
in one complete run. "Interruption = unaudited": there is no "stop at limit and
return partial findings" exit; a bounded brief is how a lane completes without
needing an interrupt. This script mechanically checks that discipline.

A brief is a fixed-structure Markdown file at `<repo>/.review-briefs/R<N>-<topic>.md`
(local work doc, gitignored). Structure (mirrors review-scope-narrowing §4.1):

    # R<N> 评审简报（<lane name>）

    ## Scope
    - base: <git ref>  head: <git ref>
    - 需深审面（精读，逐行判读）：<files this lane reads line-by-line>
    - 陪跑文件（机器门禁已盖，扫读确认即可）：<other changed files; 无 when none>
    - 门禁自证（主会话实跑，exit 随行）：<script>:<exit>，…（如 dotnet-format:0，dotnet-test:0）
    - diff 面相邻件（一层以内，按需引用）：<list or 无>

    ## 上一轮结论的处置（验轮 K≥2 必写；首轮整段省略）
    - <disposition of each Blocker/Suggestion the previous round reported>

    ## Directed checks（≤5 条）
    - [ ] <check: what to verify + where the evidence is>
    - …

    ## Explicitly out of scope
    - <what this lane must NOT do — narrows the skill's generic defaults>
    - …

    ## Report contract
    - 返回 `Blocker[]`/`Suggestion[]`，每条 `文件:行 + 一句证据`；空即"无发现"

Rules enforced:
  - lanes R1/R2/R3 each require exactly one brief under .review-briefs/
  - Scope carries `base:`/`head:`, a non-empty 需深审面 list, and a 陪跑文件 line
  - 陪跑文件 (claims machine-gated) ⇒ a 门禁自证 line must exist with all-zero
    exit codes — the "已盖" claim must be backed by the main session's real runs
    (ADR review-brief-gate-self-assertion; format exit-2 miss on 2026-09-04 made
    R2 re-verify gates it was told were green)
  - Directed checks: 1–5, each a `- [ ]` line
  - Explicitly out of scope: ≥1 line
  - Report contract: the fixed Blocker/Suggestion sentence present
  - Round discipline (ADR review-round-convergence): the round number is the
    `-r<K>` suffix on the brief filename (no suffix = round 1). For K >= 2 —
    a verification round checks dispositions, it does not re-sweep — the brief
    must carry a non-empty `## 上一轮结论的处置` section, its `base:` must be a
    TREE object (the `git write-tree` snapshot of the previous round's frozen
    index) with a non-empty delta against the index under review, and K must
    stay <= the cap (3). A round beyond the cap requires an explicit non-empty
    `轮次授权：<reason>` line, i.e. user authorization: "every round re-reads the
    whole uncommitted batch and can always find something new" is what made
    rounds diverge. Because the round comes ONLY from the filename, two
    cross-checks keep that self-declaration honest: a title declaring `第 N 轮`
    must agree with the suffix, and a brief carrying the dispositions section
    must be K >= 2. Without them, dropping the suffix silently demotes a
    verification round to round 1 and skips every K >= 2 check — including the
    tree-base rule that makes "re-read the whole batch" impossible.
  - Worktree freeze (ADR review-freeze-worktree-discipline): `git status
    --porcelain` on the real repo must show staged-only entries — a non-space
    worktree column (post-freeze edits not re-staged: the reviewed index is
    not what will be committed) or an untracked `??` line (the stray-blob
    `git add` entry form) blocks the launch. Checked in main() against the
    real repo only (the brief-shape fixtures are non-git temp dirs; the freeze
    check is covered by fixture 9's real temp git repo); `--since`
    post-hoc reviews of committed batches have no worktree semantics, so this
    never runs in CI.
  - On a frozen worktree the gate prints the round's review object as a tree
    (`git write-tree` stores it as a side effect): that SHA is the next round's
    `base:`, so pinning the delta boundary is copying a printed line instead of
    remembering a step.
  - brief files are NOT part of the git change set (gitignored); this gate is
    a local pre-launch check, not a CI gate

Default is report-only (exit 0). `--enforce` exits 1 on any violation.
`--self-test` runs offline fixtures.

Usage:
    python3 scripts/verify-review-brief.py [--repo ROOT] [--enforce]
    python3 scripts/verify-review-brief.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import subprocess
import sys
import tempfile
from pathlib import Path

BRIEFS_DIR = ".review-briefs"
LANES = ("R1", "R2", "R3")

# Title must be a level-1 heading naming exactly one lane (R1/R2/R3). The
# (?!\w) guard rejects over-matching names like "R2x 评审简报" that a bare
# character class would accept; `match` semantics anchor at line start.
TITLE_RE = re.compile(r"#\s*(?P<lane>R[123])(?!\w)\s*评审简报")
SCOPE_HEADING = "## Scope"
CHECKS_HEADING = "## Directed checks"
OUTSCOPE_HEADING = "## Explicitly out of scope"
REPORT_HEADING = "## Report contract"
REPORT_SENTENCE = "Blocker[]/Suggestion[]"
CHECK_ITEM_RE = re.compile(r"^\s*-\s*\[ \]\s+.+")
BASE_RE = re.compile(r"base:\s*\S+")
HEAD_RE = re.compile(r"head:\s*\S+")
DEEP_RE = re.compile(r"需深审面[^\n]*?[:：]")
COMPANION_RE = re.compile(r"陪跑文件[^\n]*?[:：]")
# 门禁自证行：声明陪跑文件「机器门禁已盖」必须由主会话实跑的 exit 码背书
# （ADR review-brief-gate-self-assertion）。形如「门禁自证：dotnet-format:0，dotnet-test:0」。
SELFASSERT_RE = re.compile(r"门禁自证[^\n]*?[:：]")
# 自证单项 <name>:<exit>（容忍全角冒号「：」——中文语境书写易混，S1/S2 评审发现）。name 容忍脚本
# 短名（dotnet-format/test/code-health…），exit 必须 0/1/2。
SELFASSERT_ITEM_RE = re.compile(r"([A-Za-z0-9._\-]+)[:：]([012])\b")
# 轮次纪律（ADR review-round-convergence）：轮次 = 文件名 `-r<K>` 后缀（无后缀 = 首轮）。
# 验轮只核处置闭合 + 审增量；每路每批 ≤ROUND_CAP 轮，超限须用户显式授权行。轮次只由文件名
# 取值，于是「去掉后缀」本身是一条静默降级通道（2026-09-28 R2 评审实测：标题写「第 2 轮」而
# 文件名无后缀的简报绕过全部验轮判据）——故另设两条互校：标题声明的轮次须与文件名一致；
# 处置段（本身即验轮声明）出现时轮次不得 < 2。
ROUND_RE = re.compile(r"-r(?P<round>\d+)\.md$")
# 标题声明的轮次：只认 `#` 标题行上的令牌——正文提一句「上一轮即第 1 轮报了…」不是轮次声明
# （2026-09-28 R3 评审点名：全文取首个令牌会把正文提及误读成标题声明，同时真缺令牌时互校失效）。
ROUND_DECLARED_RE = re.compile(r"^#\s.*?第\s*(?P<round>\d+)\s*轮", re.MULTILINE)
ROUND_CAP = 3
# 处置段按「行首二级标题」认：纯子串会把正文/定向检查项里对该段名的引用也算作段存在
# （本仓 2026-09-28 的 R3 简报即有此形态，R2 评审点名）。
DISPOSITIONS_RE = re.compile(r"^##\s*上一轮结论的处置", re.MULTILINE)
DISPOSITIONS_HEADING = "## 上一轮结论的处置"
# 授权行按「行首（可带列表符号）」认：规则原文本身含「轮次授权：」字样（本仓
# review-scope-narrowing §4.1、feature-flow 步骤 5 与本 ADR Decision 二都写了），子串匹配
# 等于把规则原文抄进简报即获授权（2026-09-28 R3 评审实测该形态放行）。允许 `- ` 前缀是因为
# 简报 Scope 的字段就是列表项形式。
AUTHORIZATION_RE = re.compile(r"^[-*]?\s*轮次授权[:：]\s*\S", re.MULTILINE)


def _round_of(path: Path) -> int:
    """Round number of a brief: the `-r<K>` filename suffix, else round 1."""
    m = ROUND_RE.search(path.name)
    return int(m.group("round")) if m else 1


def _violations_for_lane(path: Path, lane: str) -> list[str]:
    """Return violation strings for one brief file (empty when well-formed)."""
    v: list[str] = []
    if not path.exists():
        return [f"{lane}: missing brief {BRIEFS_DIR}/R{lane[1]}-*.md (must write brief before launching review)"]

    text = path.read_text(encoding="utf-8")

    # Title must carry the lane.
    title_match = TITLE_RE.search(text)
    if not title_match:
        v.append(f"{lane}: {path.name} lacks '# R{lane[1]} 评审简报' title")
    elif title_match.group("lane") != lane:
        v.append(f"{lane}: {path.name} title lane '{title_match.group('lane')}' != {lane}")

    # All four section headings must be present (order is not enforced).
    for heading in (SCOPE_HEADING, CHECKS_HEADING, OUTSCOPE_HEADING, REPORT_HEADING):
        if heading not in text:
            v.append(f"{lane}: {path.name} missing '{heading}' section")

    # Scope: base/head refs + non-empty deep-review file list.
    if BASE_RE.search(text) is None or HEAD_RE.search(text) is None:
        v.append(f"{lane}: {path.name} Scope must carry both 'base: <ref>' and 'head: <ref>'")
    if DEEP_RE.search(text) is None:
        v.append(f"{lane}: {path.name} Scope must list 需深审面 (files this lane reads line-by-line; empty = unbounded)")
    elif not _list_content_is_nonempty(text, DEEP_RE):
        # "需深审面：无" passes the key+colon regex but means no line-by-line
        # reading target — an unbounded-but-claiming-bounded brief must not
        # slip the gate (R1 review, 2026-09-03).
        v.append(f"{lane}: {path.name} 需深审面 must name ≥1 file (write 无 only under 陪跑文件)")
    if COMPANION_RE.search(text) is None:
        v.append(f"{lane}: {path.name} Scope must list 陪跑文件 (machine-gated files scanned only; write 无 when none)")

    # 门禁自证（ADR review-brief-gate-self-assertion）：声明「陪跑文件机器门禁已盖」必须携带
    # 主会话实跑的 exit 码背书——缺失或含非 0 都违规（"已盖"声明无实跑支撑 = 把门禁成本转嫁给
    # 评审代理实测；2026-09-04 format exit-2 漏跑教训）。「陪跑文件：无」不声明已盖，免自证。
    companion_declares_gated = COMPANION_RE.search(text) is not None and not _companion_is_none(text)
    if companion_declares_gated:
        if SELFASSERT_RE.search(text) is None:
            v.append(f"{lane}: {path.name} 陪跑文件声明机器门禁已盖，但缺「门禁自证」行——主会话须实跑门禁并随行 exit 码（如「门禁自证：dotnet-format:0，dotnet-test:0」）")
        else:
            bad = [m.group(1) for m in SELFASSERT_ITEM_RE.finditer(_selfassert_text(text)) if m.group(2) != "0"]
            if bad:
                v.append(f"{lane}: {path.name} 门禁自证含非 0 exit（{', '.join(bad)}）——红门禁文件不得列陪跑（声言已盖却实际红 = 未证；格式样例「门禁自证：dotnet-format:0，dotnet-test:0」）")

    # Directed checks: 1–5 checkbox items under the heading.
    checks_zone = _zone(text, CHECKS_HEADING, OUTSCOPE_HEADING)
    checks = [ln for ln in checks_zone.splitlines() if CHECK_ITEM_RE.match(ln)]
    if len(checks) == 0:
        v.append(f"{lane}: {path.name} Directed checks must have 1–5 '- [ ]' items (empty = unbounded review)")
    elif len(checks) > 5:
        v.append(f"{lane}: {path.name} Directed checks has {len(checks)} items (>5 — too broad to focus)")

    # Explicitly out of scope: ≥1 bullet.
    outscope_zone = _zone(text, OUTSCOPE_HEADING, REPORT_HEADING)
    outscope_lines = [ln for ln in outscope_zone.splitlines() if ln.strip().startswith(("-", "*"))]
    if len(outscope_lines) == 0:
        v.append(f"{lane}: {path.name} Explicitly out of scope must list ≥1 item (none = unbounded task)")

    # Report contract fixed sentence (backticks tolerated around the token).
    if REPORT_SENTENCE not in text.replace("`", ""):
        v.append(f"{lane}: {path.name} Report contract must carry '{REPORT_SENTENCE}'")

    # 轮次纪律（ADR review-round-convergence）：验轮只核处置闭合；轮次有界；轮次自证须互校。
    rnd = _round_of(path)
    declared = ROUND_DECLARED_RE.search(text)
    if declared and int(declared.group("round")) != rnd:
        v.append(f"{lane}: {path.name} 标题声明的第 {declared.group('round')} 轮与文件名 `-r{rnd}` 不一致"
                 "——轮次只由文件名取值，去掉后缀会把验轮静默降级为首轮（ADR review-round-convergence）")
    has_dispositions = DISPOSITIONS_RE.search(text) is not None
    if rnd >= 2 and not has_dispositions:
        v.append(f"{lane}: {path.name} 是第 {rnd} 轮但缺 '{DISPOSITIONS_HEADING}' 段"
                 "——验轮只核上一轮结论的处置闭合、不做复扫（ADR review-round-convergence）")
    elif rnd >= 2 and not _section_has_items(text, DISPOSITIONS_RE):
        v.append(f"{lane}: {path.name} 的 '{DISPOSITIONS_HEADING}' 段无条目"
                 "——空段等于没写处置，逐条处置才是验轮的对象")
    if has_dispositions and rnd < 2:
        v.append(f"{lane}: {path.name} 带 '{DISPOSITIONS_HEADING}' 段却是第 1 轮"
                 "——处置段本身即验轮声明，首轮出现即等于以批基为 base 重审全批")
    if rnd > ROUND_CAP and AUTHORIZATION_RE.search(text) is None:
        v.append(f"{lane}: {path.name} 是第 {rnd} 轮，超出上限 {ROUND_CAP} 轮"
                 "——反复审须用户显式授权：简报写出非空「轮次授权：<理由>」行才可开")

    return v


def _companion_is_none(text: str) -> bool:
    """True when the 陪跑文件 line declares 无 (no companion files claimed)."""
    m = COMPANION_RE.search(text)
    if m is None:
        return True
    line_end = text.find("\n", m.end())
    inline = text[m.end(): line_end if line_end >= 0 else len(text)].strip()
    return inline == "无" or inline.startswith("无。")


def _selfassert_text(text: str) -> str:
    """Extract the 门禁自证 line's content (after the key's colon); empty when absent.

    Non-zero-exit scanning is bound to THIS line only — the brief body may carry
    arbitrary `<token>:1` / `:2` fragments (file:line refs) that a whole-text
    finditer would misreport as a failing gate (R2/R3 review, 2026-09-04).
    """
    m = SELFASSERT_RE.search(text)
    if m is None:
        return ""
    line_end = text.find("\n", m.end())
    return text[m.end(): line_end if line_end >= 0 else len(text)]


def _zone(text: str, start_heading: str, end_heading: str) -> str:
    """Return the text between start_heading and the next end_heading (exclusive)."""
    start = text.find(start_heading)
    if start < 0:
        return ""
    end = text.find(end_heading, start + len(start_heading))
    return text[start + len(start_heading): end if end >= 0 else len(text)]


def _section_has_items(text: str, heading_re: re.Pattern[str]) -> bool:
    """True when the heading's section body carries at least one list item.

    A section that exists but is empty is not a written disposition — the same
    reason the 需深审面 key needs _list_content_is_nonempty: the heading alone is a
    shape, and shape-only evidence is what the round gate exists to refuse
    (2026-09-28 R2 review: verbatim template copies carried the heading with no
    entries underneath).
    """
    m = heading_re.search(text)
    if m is None:
        return False
    line_end = text.find("\n", m.end())
    rest = text[line_end + 1 if line_end >= 0 else len(text):]
    for ln in rest.splitlines():
        if ln.startswith("## "):
            break  # next section
        stripped = ln.strip()
        if stripped.startswith(("-", "*")) and len(stripped) > 1:
            return True
    return False


def _list_content_is_nonempty(text: str, key_re: re.Pattern[str]) -> bool:
    """True when the keyed list carries at least one named item.

    key_re matches through the key's colon (full- or half-width). Content may
    sit on the key line ("需深审面：a.cs") or as indented sub-lines under a
    bare key line (template form: "需深审面（…）：\n  - src/A.cs"). Rejects
    "需深审面：无", bare keys with nothing under them, and empty content.
    """
    m = key_re.search(text)
    if m is None:
        return False
    line_end = text.find("\n", m.end())
    inline = text[m.end(): line_end if line_end >= 0 else len(text)].strip()
    if inline != "" and inline != "无" and not inline.startswith("无。"):
        return True
    # Bare key line: look at indented sub-lines until the next top-level list
    # item or section heading.
    rest = text[line_end if line_end >= 0 else len(text):]
    for ln in rest.splitlines():
        if ln.strip() == "":
            continue
        if ln.startswith(("- ", "* ", "## ")) or not ln[0].isspace():
            break  # next top-level item / heading — list ended
        # indented sub-line: a named target (not a bare "无")
        stripped = ln.strip().lstrip("-* ").strip()
        if stripped != "" and stripped != "无" and not stripped.startswith("无。"):
            return True
    return False


def _brief_paths(repo: Path) -> tuple[dict[str, Path], list[str]]:
    """Map lane -> brief path, plus names of duplicate-lane brief files.

    Exactly one brief per lane is the contract (docstring rule "each require
    exactly one brief"); a second file for the same lane is ambiguous about
    which brief bounds the review and is reported rather than silently
    dropped (R1 review, 2026-09-03).
    """
    briefs_dir = repo / BRIEFS_DIR
    found: dict[str, Path] = {}
    duplicates: list[str] = []
    if briefs_dir.is_dir():
        for f in sorted(briefs_dir.glob("R[123]-*.md")):
            lane = f.name[0:2]  # "R1"/"R2"/"R3"
            if lane in found:
                duplicates.append(f"{lane}: multiple briefs for one lane: {found[lane].name} and {f.name}"
                                  "（一路恰一份：开验轮前归档或改名上一轮简报，否则选中的可能是旧轮件）")
            else:
                found[lane] = f
    return {lane: found.get(lane) for lane in LANES}, duplicates


def _freeze_violations(repo: Path) -> list[str]:
    """Worktree must be frozen onto the staged set (review-object freeze).

    Both the --staged tier classification and the review diff read the index;
    a worktree that has drifted from it (post-freeze edits not re-staged) or
    that carries stray untracked files (2026-09-13: an unknown-origin blob was
    `git add`ed into the index unnoticed) means the review object is not what
    will be committed. Porcelain XY semantics: any non-space worktree column,
    or an untracked `??` line, is a violation. Ignored paths (briefs, .plan)
    never appear in the output, so they cannot false-positive.
    """
    try:
        proc = subprocess.run(
            ["git", "-C", str(repo), "status", "--porcelain"],
            capture_output=True, text=True)
    except OSError:
        return ["review-freeze: cannot run git — worktree freeze unverifiable"]
    if proc.returncode != 0:
        return [f"review-freeze: git status failed: {proc.stderr.strip() or proc.returncode}"]
    out: list[str] = []
    for ln in proc.stdout.splitlines():
        if ln.startswith("?? "):
            out.append(f"review-freeze: untracked file outside the frozen review object: {ln[3:]}")
        elif len(ln) >= 2 and ln[1] != " ":
            out.append(f"review-freeze: worktree drifts from staged index (re-stage or discard before review): {ln}")
    return out


def _frozen_tree(repo: Path) -> str | None:
    """Store the frozen review object as a tree and return its SHA.

    `git write-tree` writes the index as a tree object, so this round's review
    object lands in the object store and can be cited as the next round's
    `base:` (ADR review-round-convergence). The side effect is the point: pinning
    the delta boundary becomes copying a printed line, not remembering a step.
    """
    try:
        proc = subprocess.run(["git", "-C", str(repo), "write-tree"],
                              capture_output=True, text=True)
    except OSError:
        return None
    return proc.stdout.strip() if proc.returncode == 0 else None


def _round_delta_violations(repo: Path, lanes: list[str]) -> list[str]:
    """Verification rounds (K>=2) must pin `base:` to a tree with a live delta.

    Precedent (2026-09-28): four rounds per lane ran with `base:` left at the
    batch base commit, so every round re-read the same growing uncommitted set,
    each round had something new to report, and Blocker counts rose instead of
    falling. Requiring a tree closes that without needing a commit: the batch
    base is a commit, and a tree object only comes from `git write-tree` at
    freeze time — the "just reuse the batch base" brief cannot satisfy the gate.
    """
    paths, _dups = _brief_paths(repo)
    out: list[str] = []
    for lane in lanes:
        p = paths.get(lane)
        if p is None or not p.exists() or _round_of(p) < 2:
            continue
        m = BASE_RE.search(p.read_text(encoding="utf-8"))
        if m is None:
            continue  # base presence already reported by _violations_for_lane
        base = m.group(0).split(":", 1)[1].strip()
        kind = subprocess.run(["git", "-C", str(repo), "cat-file", "-t", base],
                              capture_output=True, text=True).stdout.strip()
        if kind != "tree":
            out.append(f"{lane}: {p.name} 验轮 base '{base}' 不是 tree 对象（cat-file 得 {kind or '不可解析'}）"
                       "——须写上一轮冻结时 `git write-tree` 的 SHA；写成批基即等于重审全批")
            continue
        d = subprocess.run(["git", "-C", str(repo), "diff", "--cached", "--quiet", base],
                           capture_output=True, text=True)
        if d.returncode == 0:
            out.append(f"{lane}: {p.name} 验轮相对 base 的增量为空——本轮没有待验修复，开验轮无对象可审")
        elif d.returncode != 1:
            out.append(f"{lane}: {p.name} 验轮增量不可判定：`git diff --cached --quiet {base}` 退出码 {d.returncode}")
    return out


def _tier_lanes(repo: Path) -> list[str]:
    """Lanes required by the review tier of the current git moment.

    Reuses verify-review-tier's classification (single source of truth):
    FULL tier requires R1+R2+R3 briefs; LIGHT requires only R2.
    Falls back to R1+R2+R3 when the tier module cannot classify (conservative).

    Implicit dependency on verify-review-tier behaviour (R2 review, 2026-09-03):
      - the sibling script must be importable WITHOUT top-level side effects
        (its `if __name__ == "__main__"` guard is what makes this safe);
      - its FULL_TRIGGERS pattern `verify-*.py` matches this script itself, so
        any change to verify-review-brief.py is FULL-tier by design and needs
        Review evidence — a lane-derivation change here therefore also changes
        the gate this very script enforces. Do not silently drift these two.
    """
    try:
        import importlib.util

        script = Path(__file__).with_name("verify-review-tier.py")
        spec = importlib.util.spec_from_file_location("verify_review_tier", script)
        if spec is None or spec.loader is None:
            return list(LANES)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        tier = module
    except Exception:
        return list(LANES)  # conservative: require all three

    staged = tier._repo_changed_paths(repo, staged_only=True)
    full, _reasons = tier._classify(staged, repo)
    return list(LANES) if full else ["R2"]


def check_repo(repo: Path, lanes: list[str] | None = None) -> list[str]:
    """Return all violations across required lanes (empty when well-formed).

    lanes=None => derive from the review tier of the current git moment
    (FULL needs R1/R2/R3, LIGHT needs R2).
    """
    if lanes is None:
        lanes = _tier_lanes(repo)
    paths, duplicates = _brief_paths(repo)
    out: list[str] = list(duplicates)
    for lane in lanes:
        p = paths[lane]
        out.extend(_violations_for_lane(p, lane) if p else [f"{lane}: missing brief under {BRIEFS_DIR}/"])
    return out


def self_test() -> int:
    """Run offline fixtures; return exit code (0 = all green)."""
    failures: list[str] = []
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        # Fixture 1: well-formed R1/R2/R3 briefs -> pass.
        def w(name: str, lane: str, scope_body: str, checks: str = "- [ ] c\n",
              outscope: str = "- d\n") -> Path:
            """Write one brief fixture with fixed report contract + gate self-assertion."""
            p = root / BRIEFS_DIR / name
            p.write_text(
                f"# {lane} 评审简报（lane）\n\n## Scope\n- base: a  head: b\n{scope_body}\n"
                f"- 门禁自证：dotnet-format:0，dotnet-test:0\n\n"
                f"## Directed checks\n{checks}\n## Explicitly out of scope\n{outscope}\n"
                "## Report contract\n- 返回 `Blocker[]/Suggestion[]`；空即无发现\n",
                encoding="utf-8")
            return p

        (root / BRIEFS_DIR).mkdir()
        w("R1-a.md", "R1", "- 需深审面：src/A.cs\n- 陪跑文件：tests/B.cs\n- diff 面相邻件：无")
        w("R2-a.md", "R2", "- 需深审面：src/A.cs\n- 陪跑文件：无")
        w("R3-a.md", "R3", "- 需深审面：.agents/notes/x.md\n- 陪跑文件：无")
        if check_repo(root, lanes=list(LANES)):
            failures.append("fixture 1 (well-formed R1/R2/R3) should pass")

        # Fixture 2: missing R1 brief -> violation.
        root2 = Path(td) / "f2"
        (root2 / BRIEFS_DIR).mkdir(parents=True)
        (root2 / BRIEFS_DIR / "R2-a.md").write_text(
            "# R2 评审简报（code-review）\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n- 陪跑文件：无\n\n"
            "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n## Report contract\n"
            "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs = check_repo(root2, lanes=list(LANES))
        if not any("R1: missing brief" in s for s in vs):
            failures.append("fixture 2 (missing R1) should flag R1")

        # Fixture 3: unbounded (no out-of-scope) R2 -> violation.
        root3 = Path(td) / "f3"
        (root3 / BRIEFS_DIR).mkdir(parents=True)
        (root3 / BRIEFS_DIR / "R2-a.md").write_text(
            "# R2 评审简报（code-review）\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n- 陪跑文件：无\n\n"
            "## Directed checks\n- [ ] c\n\n"
            "## Report contract\n- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs3 = check_repo(root3, lanes=list(LANES))
        if not any("R2" in s and "out of scope" in s for s in vs3):
            failures.append("fixture 3 (no out-of-scope) should flag R2")

        # Fixture 4: >5 directed checks -> violation.
        root4 = Path(td) / "f4"
        (root4 / BRIEFS_DIR).mkdir(parents=True)
        (root4 / BRIEFS_DIR / "R1-a.md").write_text(
            "# R1 评审简报（simplifications）\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n- 陪跑文件：无\n\n"
            "## Directed checks\n" + "".join(f"- [ ] c{i}\n" for i in range(6)) +
            "\n## Explicitly out of scope\n- d\n\n## Report contract\n"
            "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs4 = check_repo(root4, lanes=list(LANES))
        if not any("R1" in s and ">5" in s for s in vs4):
            failures.append("fixture 4 (>5 checks) should flag R1")

        # Fixture 5: missing 需深审面 -> violation.
        root5 = Path(td) / "f5"
        (root5 / BRIEFS_DIR).mkdir(parents=True)
        (root5 / BRIEFS_DIR / "R2-a.md").write_text(
            "# R2 评审简报（code-review）\n\n## Scope\n- base: a  head: b\n- 陪跑文件：x\n- 门禁自证：dotnet-format:0\n\n"
            "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n## Report contract\n"
            "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs5 = check_repo(root5, lanes=list(LANES))
        if not any("R2" in s and "需深审面" in s for s in vs5):
            failures.append("fixture 5 (missing 需深审面) should flag R2")

        # Fixture 6: "需深审面：无" -> violation (non-empty list enforced).
        root6 = Path(td) / "f6"
        (root6 / BRIEFS_DIR).mkdir(parents=True)
        (root6 / BRIEFS_DIR / "R2-a.md").write_text(
            "# R2 评审简报（code-review）\n\n## Scope\n- base: a  head: b\n- 需深审面：无\n- 陪跑文件：无\n\n"
            "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n## Report contract\n"
            "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs6 = check_repo(root6, lanes=list(LANES))
        if not any("R2" in s and "需深审面 must name ≥1 file" in s for s in vs6):
            failures.append("fixture 6 (需深审面：无) should flag R2")

        # Fixture 7: 陪跑声明已盖但缺门禁自证 -> violation (gate-self-assertion).
        root7 = Path(td) / "f7"
        (root7 / BRIEFS_DIR).mkdir(parents=True)
        (root7 / BRIEFS_DIR / "R2-a.md").write_text(
            "# R2 评审简报（code-review）\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n- 陪跑文件：src/A.cs（机器门禁已盖）\n\n"
            "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n## Report contract\n"
            "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs7 = check_repo(root7, lanes=list(LANES))
        if not any("R2" in s and "门禁自证" in s for s in vs7):
            failures.append("fixture 7 (companion without gate self-assertion) should flag R2")

        # Fixture 8: 门禁自证含非 0 exit -> violation (red gate may not ride companion).
        root8 = Path(td) / "f8"
        (root8 / BRIEFS_DIR).mkdir(parents=True)
        (root8 / BRIEFS_DIR / "R2-a.md").write_text(
            "# R2 评审简报（code-review）\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n- 陪跑文件：src/A.cs（机器门禁已盖）\n"
            "- 门禁自证：dotnet-format:2，dotnet-test:0\n\n"
            "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n## Report contract\n"
            "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs8 = check_repo(root8, lanes=list(LANES))
        if not any("R2" in s and "非 0 exit" in s for s in vs8):
            failures.append("fixture 8 (self-assertion with non-zero exit) should flag R2")

        # Fixture 9: worktree freeze on a real temp git repo (ADR
        # review-freeze-worktree-discipline) — staged-only passes; a
        # post-freeze edit without re-staging and a stray untracked file
        # each violate; ignored paths never appear.
        root9 = Path(td) / "f9"
        root9.mkdir()
        def _git(*argv: str) -> None:
            subprocess.run(["git", "-C", str(root9), *argv],
                           check=True, capture_output=True, text=True)
        _git("init", "-q")
        _git("config", "user.email", "t@t")
        _git("config", "user.name", "t")
        (root9 / "a.txt").write_text("one\n", encoding="utf-8")
        (root9 / ".gitignore").write_text(".review-briefs/\n", encoding="utf-8")
        _git("add", ".")
        _git("commit", "-qm", "base")
        if _freeze_violations(root9):
            failures.append("fixture 9 (clean staged-only repo) should pass")
        (root9 / "a.txt").write_text("two\n", encoding="utf-8")
        if not any("re-stage or discard" in s for s in _freeze_violations(root9)):
            failures.append("fixture 9 (post-freeze unstaged edit) should flag worktree drift")
        _git("add", "a.txt")
        if _freeze_violations(root9):
            failures.append("fixture 9 (re-staged) should pass")
        (root9 / "stray.txt").write_text("?\n", encoding="utf-8")
        (root9 / BRIEFS_DIR).mkdir()
        (root9 / BRIEFS_DIR / "R2-a.md").write_text("x\n", encoding="utf-8")
        vs9 = _freeze_violations(root9)
        if not any("untracked file" in s and "stray.txt" in s for s in vs9):
            failures.append("fixture 9 (stray untracked file) should flag")
        if any(BRIEFS_DIR in s for s in vs9):
            failures.append("fixture 9 (ignored briefs dir) must not appear in violations")

        # Fixture 10: 验轮（-r2）缺「上一轮结论的处置」段 -> 违规；补上 -> 放行
        # （ADR review-round-convergence：验轮只核处置闭合，不复扫——2026-09-28
        # 那批连续四轮各带一次复扫，Blocker 数因此越滚越高）。
        root10 = Path(td) / "f10"
        (root10 / BRIEFS_DIR).mkdir(parents=True)
        r2_head = "# R2 评审简报（code-review）· 第 2 轮\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n- 陪跑文件：无\n\n"
        r2_tail = "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n## Report contract\n"
        (root10 / BRIEFS_DIR / "R2-a-r2.md").write_text(
            r2_head + r2_tail + "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        vs10 = check_repo(root10, lanes=["R2"])
        if not any("R2" in s and DISPOSITIONS_HEADING in s for s in vs10):
            failures.append("fixture 10 (verification round without dispositions section) should flag R2")
        (root10 / BRIEFS_DIR / "R2-a-r2.md").write_text(
            f"{r2_head}{DISPOSITIONS_HEADING}\n- B1 已改\n\n{r2_tail}- 返回 `Blocker[]/Suggestion[]`；空即无发现\n",
            encoding="utf-8")
        vs10b = check_repo(root10, lanes=["R2"])
        if vs10b:
            failures.append(f"fixture 10b (verification round with dispositions) should pass; got {vs10b}")

        # Fixture 11: 超出轮次上限（-r4）无授权行 -> 违规；带非空授权行 -> 放行
        root11 = Path(td) / "f11"
        (root11 / BRIEFS_DIR).mkdir(parents=True)
        r4_body = (f"# R1 评审简报（simplifications）· 第 4 轮\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n"
                   f"- 陪跑文件：无\n\n{DISPOSITIONS_HEADING}\n- B1 已改\n\n## Directed checks\n- [ ] c\n\n"
                   "## Explicitly out of scope\n- d\n\n## Report contract\n"
                   "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n")
        (root11 / BRIEFS_DIR / "R1-a-r4.md").write_text(r4_body, encoding="utf-8")
        vs11 = check_repo(root11, lanes=["R1"])
        if not any("R1" in s and "超出上限" in s for s in vs11):
            failures.append("fixture 11 (round 4 without authorization) should flag R1")
        (root11 / BRIEFS_DIR / "R1-a-r4.md").write_text(
            r4_body.replace("## Scope", "## Scope\n- 轮次授权：用户 2026-09-28 点名再开一轮", 1),
            encoding="utf-8")
        vs11b = check_repo(root11, lanes=["R1"])
        if any("超出上限" in s for s in vs11b):
            failures.append(f"fixture 11b (round 4 with authorization) should clear the cap; got {vs11b}")
        # 11c: 规则原文本身含「轮次授权：」字样，抄进简报不得算授权（授权行须行首锚定）。
        (root11 / BRIEFS_DIR / "R1-a-r4.md").write_text(
            r4_body.replace("## Scope",
                            "## Scope\n- 规则原文：除非简报带非空「轮次授权：」行（仅用户显式授权可写）", 1),
            encoding="utf-8")
        if not any("超出上限" in s for s in check_repo(root11, lanes=["R1"])):
            failures.append("fixture 11c (rule text quoting 轮次授权 mid-line) must not count as authorization")

        # Fixture 12: 验轮增量基准只在真 git 仓库上可判（ADR review-round-convergence）——
        # base 写成批基提交 -> 违规（等于重审全批）；base = 上一轮冻结 tree 且增量非空 -> 放行；
        # base = 当前暂存集 tree（增量为空）-> 违规。
        root12 = Path(td) / "f12"
        root12.mkdir()
        def _git12(*argv: str) -> str:
            return subprocess.run(["git", "-C", str(root12), *argv],
                                  check=True, capture_output=True, text=True).stdout.strip()
        _git12("init", "-q")
        _git12("config", "user.email", "t@t")
        _git12("config", "user.name", "t")
        (root12 / ".gitignore").write_text(f"{BRIEFS_DIR}/\n", encoding="utf-8")
        (root12 / "a.txt").write_text("one\n", encoding="utf-8")
        _git12("add", ".")
        _git12("commit", "-qm", "base")
        batch_base = _git12("rev-parse", "HEAD")
        (root12 / "a.txt").write_text("two\n", encoding="utf-8")
        _git12("add", "a.txt")
        frozen_tree = _git12("write-tree")   # 上一轮冻结时的评审对象
        (root12 / "a.txt").write_text("three\n", encoding="utf-8")
        _git12("add", "a.txt")               # 本轮修复增量
        (root12 / BRIEFS_DIR).mkdir()
        def _brief12(base: str) -> None:
            (root12 / BRIEFS_DIR / "R2-a-r2.md").write_text(
                f"# R2 评审简报（code-review）· 第 2 轮\n\n## Scope\n- base: {base}  head: 暂存集\n"
                f"- 需深审面：a.txt\n- 陪跑文件：无\n\n{DISPOSITIONS_HEADING}\n- B1 已改\n\n"
                "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n"
                "## Report contract\n- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        _brief12(batch_base)
        if not any("不是 tree 对象" in s for s in _round_delta_violations(root12, ["R2"])):
            failures.append("fixture 12 (verification base = batch commit) should flag")
        _brief12(frozen_tree)
        if _round_delta_violations(root12, ["R2"]):
            failures.append("fixture 12 (verification base = frozen tree, live delta) should pass")
        _brief12(_git12("write-tree"))       # base 等于当前暂存集 -> 增量为空
        if not any("增量为空" in s for s in _round_delta_violations(root12, ["R2"])):
            failures.append("fixture 12 (verification base = current index) should flag empty delta")

        # Fixture 13: 轮次自证互校（ADR review-round-convergence；2026-09-28 R2 评审）——轮次只由
        # 文件名后缀取值，故标题声明的轮次必须与之一致：标题写「第 2 轮」而文件名无后缀，会把
        # 以批基为 base 的验轮静默降级成首轮（该形态原先放行）。
        root13 = Path(td) / "f13"
        (root13 / BRIEFS_DIR).mkdir(parents=True)
        def _brief13(title_round: str) -> None:
            (root13 / BRIEFS_DIR / "R2-a.md").write_text(
                f"# R2 评审简报（code-review）{title_round}\n\n## Scope\n- base: a  head: b\n"
                "- 需深审面：x\n- 陪跑文件：无\n\n## Directed checks\n- [ ] c\n\n"
                "## Explicitly out of scope\n- d\n\n## Report contract\n"
                "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        _brief13("· 第 2 轮")
        if not any("与文件名" in s for s in check_repo(root13, lanes=["R2"])):
            failures.append("fixture 13 (title declares round 2, filename says round 1) should flag R2")
        _brief13("· 首轮")
        if check_repo(root13, lanes=["R2"]):
            failures.append(f"fixture 13b (title carries no round token) should pass; got {check_repo(root13, lanes=['R2'])}")
        # 13c: 轮次令牌只在正文出现（标题无令牌）不算轮次声明——标题/正文须分清。
        (root13 / BRIEFS_DIR / "R2-a.md").write_text(
            "# R2 评审简报（code-review）\n\n## Scope\n- base: a  head: b\n- 需深审面：x\n- 陪跑文件：无\n\n"
            "## Directed checks\n- [ ] 核上一轮（第 2 轮）的处置是否闭合\n\n"
            "## Explicitly out of scope\n- d\n\n"
            "## Report contract\n- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        if check_repo(root13, lanes=["R2"]):
            failures.append(f"fixture 13c (round mention in body only) should pass; got {check_repo(root13, lanes=['R2'])}")

        # Fixture 14: 处置段本身即验轮声明（同上）——两个方向：首轮带处置段判红（等于以批基重审
        # 全批）；验轮带空处置段也判红（只有标题、没有条目 = 没写处置）。
        root14 = Path(td) / "f14"
        (root14 / BRIEFS_DIR).mkdir(parents=True)
        def _brief14(name: str, title: str, body: str) -> None:
            (root14 / BRIEFS_DIR / name).write_text(
                f"# R2 评审简报（code-review）{title}\n\n## Scope\n- base: a  head: b\n"
                f"- 需深审面：x\n- 陪跑文件：无\n\n{body}\n## Directed checks\n- [ ] c\n\n"
                "## Explicitly out of scope\n- d\n\n## Report contract\n"
                "- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        _brief14("R2-a.md", "", f"{DISPOSITIONS_HEADING}\n- B1 已改\n")
        if not any("却是第 1 轮" in s for s in check_repo(root14, lanes=["R2"])):
            failures.append("fixture 14 (dispositions section on round 1) should flag R2")
        (root14 / BRIEFS_DIR / "R2-a.md").unlink()  # 一路恰一份简报：先移除首轮件再写验轮件
        _brief14("R2-b-r2.md", "· 第 2 轮", f"{DISPOSITIONS_HEADING}\n\n")
        if not any("段无条目" in s for s in check_repo(root14, lanes=["R2"])):
            failures.append("fixture 14b (empty dispositions section on a verification round) should flag R2")

        # Fixture 15: 冻结告警不吞轮次违规（2026-09-28 R2 评审）——工作树漂移 + 验轮 base 写成
        # 批基的仓库上，两类违规须在同一次报告里都出现（原短路实现要跑两遍才凑齐）。
        root15 = Path(td) / "f15"
        root15.mkdir()
        def _git15(*argv: str) -> str:
            return subprocess.run(["git", "-C", str(root15), *argv],
                                  check=True, capture_output=True, text=True).stdout.strip()
        _git15("init", "-q")
        _git15("config", "user.email", "t@t")
        _git15("config", "user.name", "t")
        (root15 / ".gitignore").write_text(f"{BRIEFS_DIR}/\n", encoding="utf-8")
        (root15 / "a.txt").write_text("one\n", encoding="utf-8")
        _git15("add", ".")
        _git15("commit", "-qm", "base")
        batch_base15 = _git15("rev-parse", "HEAD")
        (root15 / "a.txt").write_text("two\n", encoding="utf-8")
        _git15("add", "a.txt")
        (root15 / "a.txt").write_text("three\n", encoding="utf-8")  # 未暂存漂移
        (root15 / BRIEFS_DIR).mkdir()
        (root15 / BRIEFS_DIR / "R2-a-r2.md").write_text(
            f"# R2 评审简报（code-review）· 第 2 轮\n\n## Scope\n- base: {batch_base15}  head: 暂存集\n"
            f"- 需深审面：a.txt\n- 陪跑文件：无\n\n{DISPOSITIONS_HEADING}\n- B1 已改\n\n"
            "## Directed checks\n- [ ] c\n\n## Explicitly out of scope\n- d\n\n"
            "## Report contract\n- 返回 `Blocker[]/Suggestion[]`；空即无发现\n", encoding="utf-8")
        both15 = _freeze_violations(root15) + _round_delta_violations(root15, ["R2"])
        if not (any("re-stage or discard" in s for s in both15)
                and any("不是 tree 对象" in s for s in both15)):
            failures.append("fixture 15 (drift + batch-commit base) should report freeze AND round violations together")

    if failures:
        print("self-test: FAIL")
        for f in failures:
            print(" -", f)
        return 1
    print("self-test: OK (15 fixtures)")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=".", help="repo root (default cwd)")
    parser.add_argument("--enforce", action="store_true", help="exit 1 on any violation")
    parser.add_argument("--self-test", action="store_true", help="run offline fixtures")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    repo = Path(args.repo).resolve()
    lanes = _tier_lanes(repo)
    violations = check_repo(repo, lanes)
    for lane in LANES:
        lane_v = [s for s in violations if s.startswith(lane + ":")]
        for s in lane_v:
            print(s)
    freeze_v = _freeze_violations(repo)
    for s in freeze_v:
        print(s)
    # 增量判据对 index 恒有意义——评审对象就是 index，工作树漂移只说明另有未暂存改动
    # （漂移该拦，但拦它不等于增量判据失效）。原先「冻结失败即不跑增量」会让一次运行看不到
    # 全集、须跑两遍才凑齐（2026-09-28 R2 评审实测：漂移 + 验轮 base 写成批基只报 1 条）。
    round_v = _round_delta_violations(repo, lanes)
    for s in round_v:
        print(s)
    # 本轮评审对象 tree 是下一轮验轮 `base` 的唯一来源（索引移动后不可重建）：取不到即 fail
    # loud。原实现只在「无违规」分支打印 tree、取不到就静默，恰好丢在最该示警的时刻。
    tree_v: list[str] = []
    tree: str | None = None
    if not (violations or freeze_v or round_v):
        tree = _frozen_tree(repo)
        if tree is None:
            tree_v = ["review-freeze: `git write-tree` 未能取得本轮评审对象 tree"
                      "——下一轮验轮的 `base` 无从取得（索引移动后不可重建）"]
            print(tree_v[0])
    total = violations + freeze_v + round_v + tree_v
    if total:
        print(f"review-brief: {len(total)} violation(s)")
        return 1 if args.enforce else 0
    print("review-brief: OK (briefs well-formed; worktree frozen onto the staged set)")
    tree = _frozen_tree(repo)
    if tree:
        print(f"本轮评审对象 tree: {tree}（下一轮简报的 base: 引它）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
