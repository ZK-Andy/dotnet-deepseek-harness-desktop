#!/usr/bin/env python3
"""Shared helpers for the Python gate scripts (verify-*.py, coverage-summary.py).

Single home for the cross-gate duplication consolidated by the restructure
plan's batch F (ADR `process/2026-09-29-gate-common-shared-module`):

  - repo_root()            one repo-root discovery (the scripts/ dir's parent)
  - slugify/heading_slugs  GitHub-slug model for Markdown anchors, shared by
    HEADING_RE/ANCHOR_RE/LINK_RE    verify-md-links and verify-skill-format
  - CSharpLineScanner      comment/string-aware C# line cleaner, shared by
                           verify-code-health (brace counting) and
                           verify-code-conventions (D004/D005 contract scan)
  - SelfTest               the ok()/failed/banner idiom every gate self-test uses

Gate scripts import this with plain `import gate_common`: running
`python3 scripts/verify-x.py` puts the script's own directory on sys.path, so
no package or sys.path setup is needed. This module is a library, not an entry
point (no executable bit); `--self-test` runs its own offline fixtures.

Exit code 0 = self-test passed, 1 = failed, 2 = usage error.
"""

import re
import sys
from pathlib import Path

# Markdown link/anchor syntax + GitHub heading-slug model (one home; consumers
# must not re-copy these — the shell S5 shadow-copy discipline, Python side).
LINK_RE = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
HEADING_RE = re.compile(r"^(#{1,6})\s+(.+?)\s*#*\s*$")
ANCHOR_RE = re.compile(r'<a\s+id="([^"]+)"')


def repo_root() -> Path:
    """The repository root: the parent of the directory holding this module."""
    return Path(__file__).resolve().parents[1]


def slugify(text: str) -> str:
    text = text.strip().lower()
    text = re.sub(r"[^\w\u4e00-\u9fff \-]", "", text)
    text = re.sub(r"\s+", "-", text)
    return text


def heading_slugs(path: Path) -> set[str]:
    """Heading slugs and explicit `<a id>` anchors declared in one md file."""
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


class CSharpLineScanner:
    """Yield cleaned C# lines: comment text and string-literal contents blanked.

    Line/block comments are removed; normal, verbatim (@"), interpolated ($")
    and $@" string contents and char literals are each replaced by a single
    space so column offsets survive (brace counting) and regex consumers
    (contract scans) only ever see real code. A `//` inside a string literal
    is data, not a comment — blanking the literal is what keeps the rest of
    the line scannable. Block-comment state spans lines. The heuristic is
    deliberately conservative: it is a scanner for gates, not a C# lexer.
    """

    def __init__(self, lines: list[str]):
        self._lines = lines
        self._block = False  # inside /* */ spanning lines

    def _strip(self, line: str) -> tuple[str, bool]:
        out: list[str] = []
        i = 0
        n = len(line)
        in_block = self._block
        while i < n:
            c = line[i]
            nxt = line[i + 1] if i + 1 < n else ""
            if in_block:
                if c == "*" and nxt == "/":
                    in_block = False
                    i += 2
                    continue
                i += 1
                continue
            # line comment
            if c == "/" and nxt == "/":
                break
            # block comment opens
            if c == "/" and nxt == "*":
                in_block = True
                i += 2
                continue
            # string literal — start past the opening quote: called at the
            # quote itself, the scan loop would hit it immediately and return,
            # leaking the contents as code (health's pre-migration bug)
            if c == '"':
                i = self._skip_string(line, i + 1, verbatim=False)
                out.append(" ")
                continue
            # verbatim string @"  or $"  or $@" ...
            if c == "@" and nxt == '"':
                i = self._skip_string(line, i + 2, verbatim=True)
                out.append(" ")
                continue
            if c == "$" and nxt == '"':
                # regular interpolated strings use the escape dialect (\"),
                # not the verbatim one — raw=True here let a legal \" end the
                # scan early and leak the rest of the line (R2 review fix)
                i = self._skip_string(line, i + 2, verbatim=False)
                out.append(" ")
                continue
            if c == "$" and i + 2 < n and line[i + 1] == "@" and line[i + 2] == '"':
                i = self._skip_string(line, i + 3, verbatim=True)
                out.append(" ")
                continue
            # char literal
            if c == "'":
                i = self._skip_char(line, i)
                out.append(" ")
                continue
            out.append(c)
            i += 1
        self._block = in_block
        return "".join(out), in_block

    @staticmethod
    def _skip_string(line: str, i: int, verbatim: bool) -> int:
        n = len(line)
        if not verbatim:
            # escape sequences
            while i < n:
                if line[i] == "\\":
                    i += 2
                    continue
                if line[i] == '"':
                    return i + 1
                i += 1
            return n
        # verbatim: "" = escaped quote; ends at single ".
        while i < n:
            if line[i] == '"':
                if i + 1 < n and line[i + 1] == '"':
                    i += 2
                    continue
                return i + 1
            i += 1
        return n

    @staticmethod
    def _skip_char(line: str, i: int) -> int:
        n = len(line)
        # '\'' escape
        if i + 1 < n and line[i + 1] == "\\":
            i += 2
            if i < n:
                i += 1
        else:
            i += 2  # land on the closing quote (R2 review fix: +1 swallowed the rest of the line)
        if i < n and line[i] == "'":
            return i + 1
        return n

    def iter_cleaned(self):
        for line in self._lines:
            cleaned, _ = self._strip(line.rstrip("\n"))
            yield cleaned


class SelfTest:
    """Accumulate self-test verdicts with the house ok/failed/banner idiom.

    Replaces the `def ok(cond, msg)` + `nonlocal failed` closure that was
    copy-pasted across gate self-tests; `finish()` prints the banner and its
    return value is the process exit code.
    """

    def __init__(self) -> None:
        self.failed = 0

    def ok(self, cond: bool, msg: str) -> None:
        if cond:
            print(f"  ok: {msg}")
        else:
            print(f"  \u2717 {msg}", file=sys.stderr)
            self.failed = 1

    def finish(self, name: str) -> int:
        """Print the pass/fail banner and return the process exit code."""
        if self.failed == 0:
            print(f"== {name} self-test passed ==")
        else:
            print(f"== {name} self-test failed ==", file=sys.stderr)
        return self.failed


def _self_test() -> int:
    import tempfile

    st = SelfTest()

    # slug model: GitHub-style, CJK-preserving, punctuation stripped
    st.ok(slugify("Head One") == "head-one", "spaces fold to hyphens")
    st.ok(slugify("中文 标题!") == "中文-标题", "CJK kept, punctuation dropped")
    st.ok(slugify("  A  B  ") == "a-b", "outer/squeezed whitespace folds")

    with tempfile.TemporaryDirectory() as td:
        p = Path(td) / "doc.md"
        p.write_text('# T\n\n## Head One\n\n<a id="custom"></a>\n', encoding="utf-8")
        slugs = heading_slugs(p)
        st.ok(slugs == {"t", "head-one", "custom"}, f"headings + anchors: {slugs}")
        st.ok(heading_slugs(Path(td) / "absent.md") == set(),
              "unreadable file yields empty slug set")

    # scanner: comments gone, string data stays data, literals blanked
    def clean(text: str) -> list[str]:
        return list(CSharpLineScanner(text.splitlines()).iter_cleaned())

    st.ok(clean('var u = "http://x"; File.Exists(u);') ==
          ["var u =  ; File.Exists(u);"],
          "`//` inside a string is data — rest of line stays scannable")
    st.ok(clean("// gone\nint x; /* block\nstill block */ int y;") ==
          ["", "int x; ", " int y;"],
          "line + spanning block comments removed, code kept")
    st.ok("@" not in clean('var s = @"a ""b"" {x}";')[0],
          "verbatim string (with \"\" escape) blanked, @ consumed")
    st.ok("$" not in clean('var s = $"v {obj}";')[0],
          "interpolated string contents blanked, $ consumed")
    st.ok(clean("char q = '\\''; int n;") == ["char q =  ; int n;"],
          "escaped char literal blanked without eating the rest")
    st.ok(clean("var q = 'x'; int n = 1;") == ["var q =  ; int n = 1;"],
          "plain char literal blanked, rest of line stays scannable")
    st.ok(clean('var s = $"a\\"b"; int n;') == ["var s =  ; int n;"],
          "interpolated string keeps the escape dialect: \\\" does not end the scan")
    st.ok(clean('var s = "a"; /* c */ var t = "b";')[0].count("var") == 2,
          "code on both sides of an inline block comment survives")
    st.ok(clean('var s = "{ not a brace }";')[0].count("{") == 0,
          "braces inside string contents do not reach the consumer")

    # SelfTest idiom: ok() tallies, finish() banners and returns the code.
    # The intentional-failure probe writes to stderr; it is redirected so the
    # CI log of this self-test contains no ✗/failed lines (coverage-summary
    # precedent) — only the assertions' stdout verdicts stay visible.
    import contextlib
    import io

    probe = SelfTest()
    probe.ok(True, "pass branch")
    st.ok(probe.failed == 0, "pass leaves failed at 0")
    with contextlib.redirect_stderr(io.StringIO()):
        probe.ok(False, "fail branch (intentional)")
        st.ok(probe.failed == 1, "fail sets failed to 1")
        st.ok(probe.finish("probe") == 1, "finish returns the failed count as code")

    st.ok((repo_root() / "scripts" / "gate_common.py").is_file(),
          "repo_root() is the repository root (holds this module)")

    return st.finish("gate-common")


if __name__ == "__main__":
    if "--self-test" in sys.argv[1:]:
        sys.exit(_self_test())
    print("usage: python3 scripts/gate_common.py --self-test", file=sys.stderr)
    sys.exit(2)
