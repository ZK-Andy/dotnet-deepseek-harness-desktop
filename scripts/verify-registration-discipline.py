#!/usr/bin/env python3
"""Verify DI-registration discipline (architecture R1: infra types only at registration).

R1 states that concrete Infrastructure types may appear only at the
composition root's DI registration. The compiler cannot see this: the
Presentation project legally references Infrastructure, so a direct
`new HarnessRuntimeHost(...)` or `RunMarker.Acquire(...)` outside a
registration file passes build, tests, and every existing gate — only
human review catches it.

This gate closes that hole with two frozen counts over the Presentation sources
(C3 idiom: counted sites never fail by themselves — only growth past the
cap fails, so the shipped baseline is grandfathered while new coupling
is blocked):

  R1a  `new` of an Infrastructure-declared type outside registration
       files — explicit (`new Foo(`) and target-typed (`Foo x = new(`,
       attributed via the declared type on the same line, C3 idiom).
  R1b  static member access `<InfraType>.<Member>` outside registration
       files (e.g. `ProfileLifecycle.EnsureReady()`; property access counts —
       any static surface use is coupling, not just calls).
  R1q  qualified `Infrastructure....` use in code (global usings make
       qualification unnecessary, so any occurrence fails at once).

Counted R1a/R1b sites print as `note:` lines with file:line
(debuggability: which site pushed past the cap) and never fail
`--enforce` on their own. Known limitations: collection-element `new()`
(`items.Add(new() {...})`) cannot be attributed single-line and is
skipped; nested generic arguments (`new Foo<Bar<T>>(`) exceed the
one-level generic in `_NEW_RE` and are skipped; short-alias qualification
(`using Infra = ...; new Infra.Engine(`) is invisible without the
`Infrastructure` token — C3 covers the root set; the dominant
construction shapes stay covered here.

Registration files are `*Registration.cs` (per-domain `AddXxx` extensions)
plus the root set (`Program.cs` / `DesktopBootstrap*.cs`, same mouth as
verify-compose-root.py — root construction keeps counting under C3, not
here, so the two gates never double-count). `HostLog.*` is exempt: the
sanctioned ambient facility (D004 mandates logging via HostLog).

Division of labor: D005 (verify-code-conventions.py) guards BCL-level
externals (`Process`/`HttpClient`/`File`); this gate guards own-layer
crossing. Name-based matching shares the C3 precedent (a Presentation
type shadowing an Infrastructure name would misattribute — rename, not
suppress).

Default is report-only (exit 0). `--enforce` exits 1 on any violation.
`--self-test` runs offline fixtures. A line ending with
`verify-registration-discipline: ignore` suppresses that line (D005 idiom).

Usage: python3 scripts/verify-registration-discipline.py [--src DIR] [--enforce]
       python3 scripts/verify-registration-discipline.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import sys
from pathlib import Path

from gate_common import CSharpLineScanner

DEFAULT_SRC = "src/DeepSeek.Harness.Desktop"
DEFAULT_INFRA_SRC = "src/DeepSeek.Harness.Desktop.Infrastructure"
# Frozen 2026-10-01 from the real tree with this scanner: R1a = 1
# (`new StartupNoticeService` in StartupSequence.Supervision.cs — migration
# is out of this gate's scope, the cap freezes it), R1b = 15 shipped static
# sites. Zero headroom by design: any NEW direct construction or static
# call fails --enforce; lowering/raising a cap is a deliberate script edit.
DEFAULT_MAX_NEWS = 1
DEFAULT_MAX_STATIC_CALLS = 15
IGNORE_MARK = "verify-registration-discipline: ignore"

# Sanctioned cross-layer facility, not coupling (see module docstring).
EXEMPT_TYPES = {"HostLog"}

_TYPE_DECL_RE = re.compile(r"^\s*(?:[\w.]+\s+)*(?:class|struct|record|interface|enum)\s+(\w+)")
_NEW_RE = re.compile(r"\bnew\s+(\w+)(?:<[^()<>]*>)?\s*\(")
_TARGET_NEW_RE = re.compile(r"\bnew\s*\(\)")
_TARGET_OWNER_RE = re.compile(r"(?:[\w.]+\s+)?(\w+)(?:<[^=]*>)?\s+\w+\s*=\s*(?:[\w.]+\s+)?new\s*\(")
_STATIC_RE = re.compile(r"\b([A-Z]\w*)\s*\.\s*(\w+)")
_QUALIFIED_RE = re.compile(r"\bInfrastructure\s*\.[\w.]+")
_USING_DIRECTIVE_RE = re.compile(r"^\s*(?:global\s+)?using\s+[A-Za-z_][\w.]*\s*;")


def _is_registration(path: Path) -> bool:
    """Per-domain `AddXxx` extensions plus the root set (see module docstring)."""
    return (path.name.endswith("Registration.cs")
            or path.name == "Program.cs"
            or path.name.startswith("DesktopBootstrap"))


def _infra_types(infra_src: Path) -> set[str]:
    types: set[str] = set()
    for path in infra_src.rglob("*.cs"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            m = _TYPE_DECL_RE.match(line.split("//")[0])
            if m:
                types.add(m.group(1))
    return types


def _violations(src: Path, infra_src: Path,
                max_news: int, max_static_calls: int) -> tuple[list[str], list[str], int, int]:
    infra = _infra_types(infra_src)
    out: list[str] = []
    notes: list[str] = []
    news = 0
    statics = 0
    files = sorted(p for p in src.rglob("*.cs")
                   if "obj" not in p.parts and "bin" not in p.parts
                   and not _is_registration(p))
    if not files:
        return ([f"  scan set is empty under {src}"], notes, 0, 0)
    for path in files:
        try:
            raws = path.read_text(encoding="utf-8").splitlines()
        except OSError as ex:
            out.append(f"  {path.name}: unreadable ({ex})")
            continue
        rel = path.relative_to(src.parent.parent)
        for lineno, (raw, code) in enumerate(zip(raws, CSharpLineScanner(raws).iter_cleaned()), 1):
            if IGNORE_MARK in raw:
                continue
            if _USING_DIRECTIVE_RE.match(code):
                continue
            for m in _NEW_RE.finditer(code):
                if m.group(1) in infra and m.group(1) not in EXEMPT_TYPES:
                    news += 1
                    notes.append(f"  note R1a {rel}:{lineno}: `new {m.group(1)}(` outside registration")
            if _TARGET_NEW_RE.search(code):
                owner = _TARGET_OWNER_RE.search(code)
                if owner and owner.group(1) in infra and owner.group(1) not in EXEMPT_TYPES:
                    news += 1
                    notes.append(f"  note R1a {rel}:{lineno}: target-typed `new()` of `{owner.group(1)}`"
                                 " outside registration")
            for m in _STATIC_RE.finditer(code):
                if m.group(1) in infra and m.group(1) not in EXEMPT_TYPES:
                    statics += 1
                    notes.append(f"  note R1b {rel}:{lineno}: static `{m.group(1)}.{m.group(2)}`"
                                 " outside registration")
            if _QUALIFIED_RE.search(code):
                out.append(f"  R1q {rel}:{lineno}: qualified `Infrastructure....` use"
                           " (global usings cover this — qualify nothing, register everything)")
    if news > max_news:
        out.append(f"  R1a direct `new` of infra types {news} > {max_news}"
                   " (resolve via DI or move construction into AddXxx/registration)")
    if statics > max_static_calls:
        out.append(f"  R1b static infra calls {statics} > {max_static_calls}"
                   " (put a port in Core, implement in Infrastructure, inject it)")
    return out, notes, news, statics


def _self_test() -> int:
    import tempfile

    failed = 0
    with tempfile.TemporaryDirectory() as td:
        shell = Path(td) / "Shell"
        infra = Path(td) / "Infra"
        shell.mkdir(parents=True)
        infra.mkdir()

        (infra / "Engine.cs").write_text(
            "namespace Infra;\npublic sealed class Engine\n{\n"
            "    public static Engine Shared() => new Engine();\n"
            "    public void Run() { }\n"
            "}\n", encoding="utf-8")

        def run(extra_shell=(), extra_infra=(), cap_news=99, cap_statics=99):
            for name, text in extra_shell:
                (shell / name).write_text(text, encoding="utf-8")
            for name, text in extra_infra:
                (infra / name).write_text(text, encoding="utf-8")
            try:
                return _violations(shell, infra, cap_news, cap_statics)
            finally:
                for name, _ in extra_shell:
                    (shell / name).unlink()
                for name, _ in extra_infra:
                    (infra / name).unlink()

        # Passing fixture: registration construction, DI declarations,
        # HostLog use, and comment/string mentions stay silent.
        rows, notes, news, statics = run(
            extra_shell=[("EngineRegistration.cs",
                           "namespace Shell;\npublic static class EngineRegistration\n{\n"
                           "    public static object Add() => new Engine();\n"
                           "}\n"),
                          ("Worker.cs",
                           "namespace Shell;\ninternal sealed class Worker\n{\n"
                           "    private Engine _engine;\n"
                           "    public Worker(Engine engine) { _engine = engine; }\n"
                           "    public void Go() { HostLog.Write(\"Engine ready\"); }\n"
                           "    // Engine construction happens in EngineRegistration.\n"
                           "    private string _s = \"new Engine() in a string\";\n"
                           "}\n")],
            extra_infra=[("Log.cs",
                           "namespace Infra;\npublic static class HostLog\n{\n"
                           "    public static void Write(string s) { }\n"
                           "}\n")])
        if rows or notes or news or statics:
            print(f"  ✗ conforming tree reported: {rows} {notes}")
            failed = 1
        else:
            print("  ok: conforming tree -> pass")

        # R1a: explicit new outside registration (counted note, no row under cap).
        rows, notes, _, _ = run(extra_shell=[("Bad.cs",
                                        "namespace Shell;\ninternal sealed class Bad\n{\n"
                                        "    private Engine _e = new Engine();\n"
                                        "}\n")])
        if not rows and any("note R1a " in r and "new Engine(" in r for r in notes):
            print("  ok: R1a explicit new counted")
        else:
            print(f"  ✗ R1a not counted: {rows} {notes}")
            failed = 1

        # R1a: target-typed new attributed via the declared type.
        rows, notes, _, _ = run(extra_shell=[("BadTarget.cs",
                                        "namespace Shell;\ninternal sealed class BadTarget\n{\n"
                                        "    private Engine _e = new();\n"
                                        "}\n")])
        if not rows and any("target-typed `new()` of `Engine`" in r for r in notes):
            print("  ok: R1a target-typed new counted")
        else:
            print(f"  ✗ R1a target-typed not counted: {rows} {notes}")
            failed = 1

        # Collection-element new() cannot be attributed single-line: skipped,
        # never a row (known limitation, see module docstring).
        rows, notes, _, _ = run(extra_shell=[("Bare.cs",
                                        "namespace Shell;\ninternal sealed class Bare\n{\n"
                                        "    private object M() { return F(new()); }\n"
                                        "}\n")])
        if not rows and not notes:
            print("  ok: unattributable new skipped")
        else:
            print(f"  ✗ unattributable new leaked: {rows} {notes}")
            failed = 1

        # R1b: static call outside registration (counted note, no row under cap).
        rows, notes, _, _ = run(extra_shell=[("BadStatic.cs",
                                        "namespace Shell;\ninternal sealed class BadStatic\n{\n"
                                        "    public void Go() { Engine.Shared(); }\n"
                                        "}\n")])
        if not rows and any("note R1b " in r and "Engine.Shared" in r for r in notes):
            print("  ok: R1b static call counted")
        else:
            print(f"  ✗ R1b not counted: {rows} {notes}")
            failed = 1

        # R1a: one-level generic new is attributed (`new EngineBox<string>(`).
        (infra / "Box.cs").write_text(
            "namespace Infra;\npublic sealed class EngineBox<T>\n{\n}\n", encoding="utf-8")
        rows, notes, _, _ = run(extra_shell=[("BadGeneric.cs",
                                        "namespace Shell;\ninternal sealed class BadGeneric\n{\n"
                                        "    private EngineBox<string> _b = new EngineBox<string>();\n"
                                        "}\n")])
        if not rows and any("note R1a " in r and "new EngineBox(" in r for r in notes):
            print("  ok: R1a generic new counted")
        else:
            print(f"  ✗ R1a generic not counted: {rows} {notes}")
            failed = 1
        (infra / "Box.cs").unlink()

        # R1q: fully qualified construction trips at once (pinned: _NEW_RE
        # cannot see past the dotted path, R1q is the only net).
        rows, _, _, _ = run(extra_shell=[("BadQualified.cs",
                                        "namespace Shell;\ninternal sealed class BadQualified\n{\n"
                                        "    private object M() => DeepSeek.Harness.Desktop.Infrastructure.Engine.Build();\n"
                                        "}\n")])
        if any(r.startswith("  R1q ") for r in rows):
            print("  ok: R1q qualified use flagged")
        else:
            print(f"  ✗ R1q not flagged: {rows}")
            failed = 1

        # `global using` directives are skipped like plain usings.
        rows, notes, _, _ = run(extra_shell=[("UsesGlobal.cs",
                                        "global using System;\nnamespace Shell;\ninternal sealed class UsesGlobal\n{\n"
                                        "}\n")])
        if not rows and not notes:
            print("  ok: global using skipped")
        else:
            print(f"  ✗ global using leaked: {rows} {notes}")
            failed = 1

        # Ignore marker suppresses both checks on its line.
        rows, notes, news, statics = run(extra_shell=[("Suppressed.cs",
                                                 "namespace Shell;\ninternal sealed class Suppressed\n{\n"
                                                 "    private Engine _e = new Engine();"
                                                 " // verify-registration-discipline: ignore 迁移中\n"
                                                 "    public void Go() { Engine.Shared(); }"
                                                 " // verify-registration-discipline: ignore 迁移中\n"
                                                 "}\n")])
        if rows or notes or news or statics:
            print(f"  ✗ ignore marker not honored: {rows} {notes}")
            failed = 1
        else:
            print("  ok: ignore marker suppresses a line")

        # Caps: over-cap fails with a cap row.
        rows, _, _, _ = run(extra_shell=[("CapA.cs",
                                        "namespace Shell;\ninternal sealed class CapA\n{\n"
                                        "    private Engine _a = new Engine();\n"
                                        "    private Engine _b = new Engine();\n"
                                        "}\n")],
                          cap_news=1)
        if any(r.startswith("  R1a direct `new`") for r in rows):
            print("  ok: R1a cap flagged")
        else:
            print(f"  ✗ R1a cap not flagged: {rows}")
            failed = 1

    if failed == 0:
        print("== verify-registration-discipline self-test passed ==")
    else:
        print("== verify-registration-discipline self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(description="Verify DI-registration discipline (R1)")
    parser.add_argument("--src", default=DEFAULT_SRC,
                        help="Presentation project root (scan home)")
    parser.add_argument("--infra-src", default=DEFAULT_INFRA_SRC,
                        help="Infrastructure type-declaration scan root")
    parser.add_argument("--max-news", type=int, default=DEFAULT_MAX_NEWS,
                        help="cap on direct `new` of infra types outside registration")
    parser.add_argument("--max-static-calls", type=int, default=DEFAULT_MAX_STATIC_CALLS,
                        help="cap on static infra calls outside registration")
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any violation (default: report only)")
    args = parser.parse_args()

    rows, notes, news, statics = _violations(Path(args.src), Path(args.infra_src),
                                      args.max_news, args.max_static_calls)
    print(f"  infra direct-new count: {news} (cap {args.max_news})")
    print(f"  infra static-call count: {statics} (cap {args.max_static_calls})")
    for n in notes:
        print(n)
    if rows:
        print(f"registration-discipline: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        if args.enforce:
            return 1
    else:
        print("registration-discipline: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
