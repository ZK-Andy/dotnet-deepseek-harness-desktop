#!/usr/bin/env python3
"""Verify source-code size health (architecture R4 + compose-root R1).

Implements the size health gate from the architecture-mechanization ADR
(`.agents/notes/proposed/process/2026-08-30-architecture-mechanization.md`):

  F1  any file > `--file-limit` physical lines (default 400)
  F2  any method > `--method-limit` lines (default 80)
  F3  compose-root file (DesktopBootstrap*.cs + Program.cs) > `--file-limit`
  F4  compose-root method > `--compose-method-limit` lines (default 60)

A method is a brace-matched body whose opening line looks like a C# method
signature. Allman braces (the `{` on the line after the signature) and inline
braces are both handled. Comments and string literal *contents* are stripped
before brace counting so `{`/`}` inside a string or comment does not skew
nesting. The heuristic is deliberately conservative: health-gate line counts
are a nudge, not a spec, and the thresholds are generous.

`// verify-code-health: ignore` on the enclosing file/method suppresses a
single reported item (an explicit, named exemption — nothing is silently
relaxed).

Default is report-only (prints violations, exit 0). `--enforce` exits 1 on any
violation. `--self-test` runs offline synthetic fixtures.

Usage: python3 scripts/verify-code-health.py [--src DIR] [--enforce]
       python3 scripts/verify-code-health.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import sys
from pathlib import Path

DEFAULT_FILE_LIMIT = 400
DEFAULT_METHOD_LIMIT = 80
DEFAULT_COMPOSE_METHOD_LIMIT = 60
IGNORE_MARK = "verify-code-health: ignore"

CONTROL_KEYWORDS = {
    "if", "for", "foreach", "while", "switch", "catch", "using", "lock",
    "fixed", "else", "return", "throw", "yield", "goto", "do",
}
TYPE_KEYWORDS = {
    "class", "struct", "interface", "enum", "record", "namespace", "delegate",
}

_TYPE_DECL_RE = re.compile(
    r"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|"
    r"partial|readonly|unsafe|ref|file|new|record|init)\s+)*"
    r"(?:class|struct|interface|enum|record|delegate|namespace)\b")


def _classify_api(api: str) -> tuple[str, str]:
    """Split a two-segment `<kind>/<api>` path into (kind, api-path)."""
    parts = api.split("/", 1)
    return (parts[0], parts[1] if len(parts) > 1 else parts[0])


class _LineScanner:
    """Yield cleaned lines (comments/string-literal contents removed)."""

    # States: code, line-comment, block-comment, string (normal), verbatim
    # string, char. Used to drop braces that are inside a literal/comment so
    # the brace-depth walker only sees real code braces.
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
            # string literal
            if c == '"':
                i = self._skip_string(line, i, verbatim=False)
                out.append(" ")
                continue
            # verbatim string @"  or $"  or $@" ...
            if c == "@" and nxt == '"':
                i = self._skip_string(line, i + 2, verbatim=True)
                out.append(" ")
                continue
            if c == "$" and nxt == '"':
                i = self._skip_string(line, i + 2, verbatim=False, raw=True)
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
    def _skip_string(line: str, i: int, verbatim: bool, raw: bool = False) -> int:
        n = len(line)
        if not verbatim and not raw:
            # escape sequences
            while i < n:
                if line[i] == "\\":
                    i += 2
                    continue
                if line[i] == '"':
                    return i + 1
                i += 1
            return n
        # verbatim/interpolated: "" = escaped quote; ends at single ".
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
            i += 1
        if i < n and line[i] == "'":
            return i + 1
        return n

    def iter_cleaned(self):
        for line in self._lines:
            cleaned, _ = self._strip(line.rstrip("\n"))
            yield cleaned


def _header_is_method(pending: list[str]) -> bool:
    """Classify a brace-block header as a C# method declaration.

    `pending` is the list of cleaned line-texts accumulated since the last
    statement terminator (`;`), closing brace, or block opener at this scope —
    i.e. the text that precedes the `{` about to open. It is robust to
    multi-line signatures (Allman parameters on their own lines) and to tuple
    return types. A method must have a top-level parenthesised parameter list
    whose closing `)` ends the header, and must not be a control statement,
    type declaration, property, or lambda/assignment.
    """
    # strip leading lines that are pure attributes `[ ... ]`
    while pending and pending[0].startswith("["):
        pending = pending[1:]
    text = " ".join(p.strip() for p in pending).strip()
    if not text:
        return False
    parts = text.split()
    first = parts[0].lower() if parts else ""
    if not parts:
        return False
    if first in CONTROL_KEYWORDS or first in TYPE_KEYWORDS or first == "new":
        return False
    if _TYPE_DECL_RE.match(text):
        return False
    if text.startswith("(") or "=>" in text:
        return False
    # reject assignment/field initializers: the declaration part before the
    # parameter list must not contain '=' (default parameters live inside the
    # parens, so we only look at the text before the first '(').
    if "=" in text.split("(", 1)[0]:
        return False
    if "(" not in text:
        return False
    # the parameter list must close at the end of the header
    return text.rstrip().endswith(")")


def _method_spans(lines: list[str]) -> list[tuple[int, int]]:
    """Return (start, end) 1-based line spans for every brace method body.

    Uses a brace-walk that classifies each `{` block by the header text that
    precedes it. `;` and a closing `}` terminate the pending header; a `{`
    opens the block and consumes it. This is robust to Allman braces and to
    signatures spanning multiple lines.
    """
    scanner = _LineScanner(lines)
    cleaned = list(scanner.iter_cleaned())
    spans: list[tuple[int, int]] = []
    stack: list[tuple[bool, int | None]] = []  # (is_method, header_start_lineno)
    pending: list[str] = []
    pending_start: int | None = None

    def flush() -> None:
        nonlocal pending, pending_start
        pending = []
        pending_start = None

    for lineno, line in enumerate(cleaned):
        buf: list[str] = []

        def _emit(start_lineno: int, b: list[str]) -> bool:
            """Flush non-whitespace buf into pending; return whether emitted."""
            nonlocal pending_start
            part = "".join(b).strip()
            if not part:
                return False
            pending.append(part)
            if pending_start is None:
                pending_start = start_lineno + 1
            return True

        for ch in line:
            if ch == "{":
                _emit(lineno, buf)
                buf = []
                stack.append((_header_is_method(pending), pending_start))
                flush()
            elif ch == "}":
                _emit(lineno, buf)
                buf = []
                if stack:
                    is_method, start = stack.pop()
                    if is_method and start is not None:
                        spans.append((start, lineno + 1))
                flush()
            elif ch == ";":
                _emit(lineno, buf)
                buf = []
                flush()
            else:
                buf.append(ch)
        _emit(lineno, buf)
    return spans


def _has_ignore(span: tuple[int, int], lines: list[str]) -> bool:
    """A file/method is exempt when an `// verify-code-health: ignore` line is
    found on the method's signature/body lines or the file header."""
    for idx in range(span[0] - 1, min(span[1], len(lines))):
        if IGNORE_MARK in lines[idx]:
            return True
    return False


def _file_ignore(lines: list[str], limit: int = 12) -> bool:
    for line in lines[:limit]:
        if IGNORE_MARK in line:
            return True
    return False


def _violations(path: Path, file_limit: int, method_limit: int,
                compose_method_limit: int) -> list[str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    out: list[str] = []
    total = len(lines)
    is_compose = path.name == "Program.cs" or path.name.startswith("DesktopBootstrap")

    if total > file_limit and not (is_compose and False):
        # F1 applies to all files; F3 (compose-root) tightens nothing beyond
        # the same file-limit but is reported distinctly for clarity.
        out.append(f"  {path.name}: F1 {total} lines > {file_limit}")

    if is_compose and total > file_limit:
        out.append(f"  {path.name}: F3 compose-root {total} lines > {file_limit}")

    limit = compose_method_limit if is_compose else method_limit
    if _file_ignore(lines):
        return out  # whole file exempt from method checks

    for start, end in _method_spans(lines):
        span = end - start + 1
        if span > limit and not _has_ignore((start - 1, end - 1), lines):
            kind = "F4" if is_compose else "F2"
            out.append(f"  {path.name}:{start}: {kind} method {span} lines > {limit}")
    return out


def _scan(src: Path, file_limit: int, method_limit: int,
          compose_method_limit: int) -> list[str]:
    rows: list[str] = []
    for path in sorted(src.rglob("*.cs")):
        rel = path.relative_to(src)
        if any(part in ("obj", "bin") for part in rel.parts):
            continue
        for v in _violations(path, file_limit, method_limit, compose_method_limit):
            rows.append(v)
    return rows


def _self_test() -> int:
    import tempfile

    cases = [
        # (lines, expected_violation_count, desc)
        ([
            "namespace Foo;",
            "public class C {",
            "    public void OK(int x) {",
            "        return;",
            "    }",
            "}",
        ], 0, "conforming small file -> pass"),
        ([
            "namespace Foo;",
            "public class C {",
            "    public void Big() {",
            "        var i = 1;",
            "        var j = 2;",
            "    }",
            "}",
        ], 0, "small method -> pass"),
    ]
    # 2) a > file-limit / method-limit fixture is generated inline below.
    failed = 0
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        # passing case
        (root / "Pass.cs").write_text("\n".join(cases[0][0]) + "\n", encoding="utf-8")
        (root / "Pass2.cs").write_text("\n".join(cases[1][0]) + "\n", encoding="utf-8")
        rows = _scan(root, DEFAULT_FILE_LIMIT, DEFAULT_METHOD_LIMIT,
                     DEFAULT_COMPOSE_METHOD_LIMIT)
        if rows:
            print(f"  ✗ conforming files reported: {rows}")
            failed = 1
        else:
            print("  ok: conforming files -> pass")

        # failing file-limit case
        big_file = ["namespace Foo;"] + \
            ["public class C {"] + \
            [f"    // line {i}" for i in range(410)] + \
            ["}"]
        (root / "BigFile.cs").write_text("\n".join(big_file) + "\n", encoding="utf-8")
        rows = _scan(root, DEFAULT_FILE_LIMIT, DEFAULT_METHOD_LIMIT,
                     DEFAULT_COMPOSE_METHOD_LIMIT)
        if any("F1" in r for r in rows):
            print("  ok: F1 file-limit violation flagged")
        else:
            print(f"  ✗ F1 not flagged: {rows}")
            failed = 1

        # failing method case (a method whose body exceeds method-limit)
        body = ["    public void Big() {"] + [
            f"        var i{n} = {n};" for n in range(90)
        ] + ["    }"]
        big_method = ["namespace Foo;", "public class C {"] + body + ["}"]
        (root / "BigMethod.cs").write_text("\n".join(big_method) + "\n",
                                           encoding="utf-8")
        rows = _scan(root, DEFAULT_FILE_LIMIT, DEFAULT_METHOD_LIMIT,
                     DEFAULT_COMPOSE_METHOD_LIMIT)
        if any("F2" in r for r in rows):
            print("  ok: F2 method-limit violation flagged")
        else:
            print(f"  ✗ F2 not flagged: {rows}")
            failed = 1

        # compose-root method: a DesktopBootstrap file whose method exceeds
        # the tighter compose limit even though it is under the general one.
        compose_body = ["    public void Big() {"] + [
            f"        var i{n} = {n};" for n in range(70)
        ] + ["    }"]
        compose = ["namespace Foo;", "public class C {"] + compose_body + ["}"]
        (root / "DesktopBootstrap.cs").write_text("\n".join(compose) + "\n",
                                                  encoding="utf-8")
        rows = _scan(root, DEFAULT_FILE_LIMIT, DEFAULT_METHOD_LIMIT,
                     DEFAULT_COMPOSE_METHOD_LIMIT)
        if any("F4" in r for r in rows):
            print("  ok: F4 compose-root method violation flagged")
        else:
            print(f"  ✗ F4 not flagged: {rows}")
            failed = 1

    if failed == 0:
        print("== verify-code-health self-test passed ==")
    else:
        print("== verify-code-health self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(description="Verify src code-size health")
    parser.add_argument("--src", default="src/DeepSeek.Harness.Desktop")
    parser.add_argument("--file-limit", type=int, default=DEFAULT_FILE_LIMIT)
    parser.add_argument("--method-limit", type=int, default=DEFAULT_METHOD_LIMIT)
    parser.add_argument("--compose-method-limit", type=int,
                        default=DEFAULT_COMPOSE_METHOD_LIMIT)
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any violation (default: report only)")
    args = parser.parse_args()

    rows = _scan(Path(args.src), args.file_limit, args.method_limit,
                 args.compose_method_limit)
    if rows:
        print(f"code-health: {len(rows)} size violation(s)")
        for r in rows:
            print(r)
        if args.enforce:
            return 1
    else:
        print("code-health: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
