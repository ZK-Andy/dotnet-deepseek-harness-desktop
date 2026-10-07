#!/usr/bin/env python3
"""Verify compose-root semantics (architecture R1: the root only assembles).

Complements the size gates (verify-code-health.py): F3 caps what the root set
WEIGHS in aggregate; this gate caps what the root HOLDS. The regression it
guards against (ADR compose-root-form-separation, replaying 1434271): stage
methods and business predicates accrete on the composition root as new partial
files, each dodging a per-file size budget.

The root set is `Program.cs` + `DesktopBootstrap*.cs` under the Presentation
project. Four checks:

  C1  dot partials ≤ `--max-dot-partials` (default 1): the root is pinned to
      Program.cs + DesktopBootstrap.cs + at most one dot partial.
  C2  every method declared in the root set must be in the ALLOWED_METHODS
      inventory. A NEW method name in the root is a violation — orchestration
      belongs in `Bootstrap/StartupSequence` or a domain service; a genuinely
      assembly-level method is added here deliberately (update inventory + ADR).
  C3  `new` of self-owned concrete types in the root ≤ `--max-new` (default 16).
      Self-owned = a type declared anywhere under `--owned-src` (Core/
      Infrastructure/Presentation); Ryn/BCL types do not count. Target-typed
      `new()` is attributed via the assigned field/property type on the same
      line; an unattributable one is reported, not skipped.
  C4  no sync-over-async in the root: `.GetAwaiter().GetResult()` lived on the
      root's stage methods historically and must stay in the orchestration
      service after the separation.

Default is report-only (exit 0). `--enforce` exits 1 on any violation.
`--self-test` runs offline fixtures.

Usage: python3 scripts/verify-compose-root.py [--src DIR] [--enforce]
       python3 scripts/verify-compose-root.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import re
import sys
from pathlib import Path

from gate_common import CSharpLineScanner, TARGET_OWNER_RE, TYPE_DECL_RE

DEFAULT_SRC = "src/DeepSeek.Harness.Desktop"
DEFAULT_OWNED_SRC = (
    "src/DeepSeek.Harness.Desktop",
    "src/DeepSeek.Harness.Desktop.Core",
    "src/DeepSeek.Harness.Desktop.Infrastructure",
)
DEFAULT_MAX_NEW = 16
DEFAULT_MAX_DOT_PARTIALS = 1

# Assembly-level surface of the root, frozen at the form separation
# (ADR compose-root-form-separation). Entries are bare method names; a name
# here asserts "this method is assembly/startup-head, not orchestration".
ALLOWED_METHODS = {
    # Program.cs — CLI branch only.
    "Main",
    "ExportDiagnostics",
    # DesktopBootstrap.cs — container-before startup head.
    "Run",
    "ResolveSharedState",
    "ResolveRuntimeAndDev",
    "AcquireSingleInstance",
    "StartProxy",
    # DesktopBootstrap.App.cs — app assembly + registration call site.
    "InitCloseGateAndUpdateStack",
    "BuildApp",
    "RegisterServices",
}

_NEW_RE = re.compile(r"\bnew\s+(\w+)\s*\(")
# `Type field = new();` — the assigned declared type gives target-typed new its owner.
_TARGET_NEW_RE = re.compile(r"\bnew\s*\(\)")
_GETAWAITER_RE = re.compile(r"\.GetAwaiter\(\)\.GetResult\(\)")
_COMPOSE_METHOD_RE = re.compile(r"^\s*(?:public|private|protected|internal)\s+(?:static\s+)?(?:\([^;{}]*\)\s+)?(?:[\w<>\[\],.?]+\s+)*(\w+)\s*\(")
# Constructors (single token between modifiers and paren) and expression-bodied
# members (`Type Name => ...`) both carry logic and belong in the C2 inventory.
_COMPOSE_CTOR_RE = re.compile(r"^\s*(?:public|private|protected|internal)\s+(\w+)\s*\(")
_COMPOSE_PROPERTY_RE = re.compile(r"^\s*(?:public|private|protected|internal)\s+[\w<>\[\],.?]+\s+(\w+)\s*=>")


def _root_files(src: Path) -> list[Path]:
    return sorted(p for p in src.rglob("*.cs")
                  if "obj" not in p.parts and "bin" not in p.parts
                  and (p.name == "Program.cs" or p.name.startswith("DesktopBootstrap")))


def _owned_types(owned_srcs: list[Path]) -> set[str]:
    types: set[str] = set()
    for src in owned_srcs:
        for path in src.rglob("*.cs"):
            if any(part in ("obj", "bin") for part in path.relative_to(src).parts):
                continue
            raws = path.read_text(encoding="utf-8").splitlines()
            for code in CSharpLineScanner(raws).iter_cleaned():
                m = TYPE_DECL_RE.match(code)
                if m:
                    types.add(m.group(1))
    return types


def _violations(src: Path, owned_srcs: list[Path],
                max_new: int, max_dot_partials: int) -> tuple[list[str], int]:
    files = _root_files(src)
    if not files:
        return ([f"  compose-root set is empty under {src} (Program.cs / DesktopBootstrap*.cs)"], 0)

    out: list[str] = []
    dot_partials = [p.name for p in files if p.name != "Program.cs" and "." in p.stem]
    if len(dot_partials) > max_dot_partials:
        out.append(
            f"  C1 {len(dot_partials)} dot partials > {max_dot_partials}: {', '.join(dot_partials)}"
            " (orchestration belongs in Bootstrap/StartupSequence or a domain service)")

    owned = _owned_types(owned_srcs)
    new_count = 0
    for path in files:
        raws = path.read_text(encoding="utf-8").splitlines()
        for code in CSharpLineScanner(raws).iter_cleaned():
            for m in _NEW_RE.finditer(code):
                if m.group(1) in owned:
                    new_count += 1
            if _TARGET_NEW_RE.search(code):
                owner = TARGET_OWNER_RE.search(code)
                if owner and owner.group(1) in owned:
                    new_count += 1
                elif owner is None:
                    out.append(
                        f"  C3 {path.name}: unattributable target-typed `new()` "
                        "(attribute the declared type or write it explicitly)")
            if _GETAWAITER_RE.search(code):
                out.append(f"  C4 {path.name}: sync-over-async `.GetAwaiter().GetResult()` in root")
            matched = None
            for pattern in (_COMPOSE_METHOD_RE, _COMPOSE_CTOR_RE, _COMPOSE_PROPERTY_RE):
                m = pattern.match(code)
                if m:
                    matched = m.group(1)
                    break
            if matched and matched not in ALLOWED_METHODS:
                out.append(
                    f"  C2 {path.name}: root member `{matched}` is not in the assembly inventory"
                    " (move to Bootstrap/StartupSequence or a domain service; "
                    "assembly-level additions update ALLOWED_METHODS deliberately)")

    if new_count > max_new:
        out.append(f"  C3 root `new` of self-owned types {new_count} > {max_new}")
    return out, new_count


def _self_test() -> int:
    import tempfile

    failed = 0
    with tempfile.TemporaryDirectory() as td:
        root = Path(td) / "Shell"
        root.mkdir(parents=True)
        domain = root / "Bootstrap"
        domain.mkdir()

        def base_program(extra=""):
            return f"""namespace Shell;
public static class Program
{{
    public static int Main(string[] args) => new ShellEntry().Run();{extra}
}}
"""

        # Passing fixture: inventory methods, few news, one dot partial, no GetAwaiter.
        (root / "Program.cs").write_text(base_program(), encoding="utf-8")
        (root / "DesktopBootstrap.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopBootstrap
{
    private ShellEntry _entry = new ShellEntry();
    public int Run() { return _entry.Run(); }
    private void StartProxy() { }
}
""", encoding="utf-8")
        (domain / "StartupSequence.cs").write_text(
            """namespace Shell.Bootstrap;
internal sealed class StartupSequence
{
    public int Run() { return _ = 0; }
    private int Wait() { return 1; }
}
""", encoding="utf-8")
        rows, count = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if rows:
            print(f"  ✗ conforming root reported: {rows}")
            failed = 1
        else:
            print("  ok: conforming root -> pass")

        # C2: a new method on the root (orchestration accretion).
        (root / "DesktopBootstrap.App.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopBootstrap
{
    private void EnsureDesktopProfile() { }
}
""", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C2 " in r and "EnsureDesktopProfile" in r for r in rows):
            print("  ok: C2 unknown root method flagged")
        else:
            print(f"  ✗ C2 not flagged: {rows}")
            failed = 1
        (root / "DesktopBootstrap.App.cs").unlink()

        # C2: tuple-returning methods resolve to the method name, not `static`
        # (2b ResolveSharedState returns a value tuple; the return parens must
        # not shadow the method name).
        (root / "DesktopBootstrap.App.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopBootstrap
{
    private static (int A, int B) MakePair() => (1, 2);
}
""", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C2 " in r and "MakePair" in r for r in rows):
            print("  ok: C2 tuple-returning root method flagged by name")
        else:
            print(f"  ✗ C2 tuple return not named: {rows}")
            failed = 1
        (root / "DesktopBootstrap.App.cs").unlink()

        # C1: two dot partials exceed the cap of one.
        (root / "DesktopBootstrap.App.cs").write_text(
            "namespace Shell;\npublic sealed partial class DesktopBootstrap\n{\n}\n", encoding="utf-8")
        (root / "DesktopBootstrap.Extra.cs").write_text(
            "namespace Shell;\npublic sealed partial class DesktopBootstrap\n{\n}\n", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C1 " in r for r in rows):
            print("  ok: C1 extra dot partial flagged")
        else:
            print(f"  ✗ C1 not flagged: {rows}")
            failed = 1
        (root / "DesktopBootstrap.App.cs").unlink()
        (root / "DesktopBootstrap.Extra.cs").unlink()

        # C3: new-of-owned over the cap (fixture cap = 1; ShellEntry twice).
        (domain / "ShellEntry.cs").write_text(
            "namespace Shell.Bootstrap;\npublic sealed class ShellEntry\n{\n}\n", encoding="utf-8")
        (root / "DesktopBootstrap.C3.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopBootstrap
{
    private ShellEntry _a = new ShellEntry();
    private ShellEntry _b = new ShellEntry();
    public int Resolve() { return 0; }
}
""", encoding="utf-8")
        rows, _ = _violations(root, [root], 1, DEFAULT_MAX_DOT_PARTIALS)
        if any(r.startswith("  C3 root `new`") for r in rows):
            print("  ok: C3 new-of-owned cap flagged")
        else:
            print(f"  ✗ C3 not flagged: {rows}")
            failed = 1
        (root / "DesktopBootstrap.C3.cs").unlink()

        # C3 (target-typed): `Type field = new();` counts via the declared type;
        # an unattributable `new()` is reported, not skipped.
        # 基线计数取自跨夹具残留（Program/DesktopBootstrap 的 ShellEntry 构造）——
        # 断言以增量锚定，避免夹具间耦合读数漂移。
        _, base_count = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        (root / "DesktopBootstrap.TN.cs").write_text(
            """
namespace Shell;
public sealed partial class DesktopBootstrap
{
    private ShellEntry _c = new();
    private ShellEntry _d = new();
    private ShellEntry _e = Make();
    private ShellEntry Make() { return F(new()); }
    public int Run() { return 0; }
}
""", encoding="utf-8")
        rows, count = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if count == base_count + 2 and any("unattributable target-typed" in r for r in rows):
            print("  ok: C3 target-typed new attributed and unattributable flagged")
        else:
            print(f"  ✗ C3 target-typed wrong (count={count}, rows={rows})")
            failed = 1
        (root / "DesktopBootstrap.TN.cs").unlink()

        # C4: sync-over-async in the root.
        (root / "DesktopBootstrap.C4.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopBootstrap
{
    public int Run() { return 0; }
    private void Boot() { var x = Wait().GetAwaiter().GetResult(); }
}
""", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C4 " in r for r in rows):
            print("  ok: C4 sync-over-async flagged")
        else:
            print(f"  ✗ C4 not flagged: {rows}")
            failed = 1

    if failed == 0:
        print("== verify-compose-root self-test passed ==")
    else:
        print("== verify-compose-root self-test failed ==", file=sys.stderr)
    return failed


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        return _self_test()

    parser = argparse.ArgumentParser(description="Verify compose-root semantics (R1)")
    parser.add_argument("--src", default=DEFAULT_SRC, help="Presentation project root (root-set home)")
    parser.add_argument("--owned-src", nargs="+",
                        default=DEFAULT_OWNED_SRC,
                        help="type-declaration scan roots for the C3 owned-type set")
    parser.add_argument("--max-new", type=int, default=DEFAULT_MAX_NEW,
                        help="cap on `new` of self-owned concrete types in the root")
    parser.add_argument("--max-dot-partials", type=int, default=DEFAULT_MAX_DOT_PARTIALS)
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any violation (default: report only)")
    args = parser.parse_args()

    rows, new_count = _violations(Path(args.src), [Path(s) for s in args.owned_src],
                                  args.max_new, args.max_dot_partials)
    print(f"  root new-of-owned count: {new_count} (cap {args.max_new})")
    if rows:
        print(f"compose-root: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        if args.enforce:
            return 1
    else:
        print("compose-root: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
