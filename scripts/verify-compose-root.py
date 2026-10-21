#!/usr/bin/env python3
"""Verify compose-root semantics (architecture R1: the root only assembles).

Complements the size gates (verify-code-health.py): F3 caps what the root set
WEIGHS in aggregate; this gate caps what the root HOLDS. The regression it
guards against (ADR compose-root-form-separation, replaying 1434271): stage
methods and business predicates accrete on the composition root as new partial
files, each dodging a per-file size budget.

Two scopes, declared in this file (the single home for these judgements):

  ROOT_SET       = `Program.cs` + `DesktopAdapter*.cs`. Weight and self-造假
                   metrics: F3 budget (verify-code-health), F4, C3, C4, C5.
  ASSEMBLY_SURFACE = ROOT_SET + ASSEMBLY_SURFACE_EXTRA (`Bootstrap/PreBuildRegistration.cs`,
                   `Bootstrap/ShellResources.cs`, `RynOptionsProfile.cs`). Direction
                   gates: C1 + C2 over the whole surface, C7 over
                   `PreBuildRegistration*.cs`, C8 over new `Bootstrap/` files (a file
                   that is neither domain nor declared surface fails). Assembly code
                   must not escape the gates by moving out of the root files; adding a
                   file to either list is a gate change (FULL review).

Seven checks:

  C1  dot partials ≤ `--max-dot-partials` (default 1) across the assembly surface:
      each surface file is a single file, no dot-partial siblings.
  C2  every member declared in an assembly-surface file must be in that file's
      ALLOWED_METHODS entry (per-file binding: a name licensed in one file does not
      license it elsewhere). A NEW member is a violation — orchestration
      belongs in `Bootstrap/StartupSequence` or a domain service; a genuinely
      assembly-level method is added here deliberately (update inventory + ADR).
  C3  `new` of self-owned concrete types in the root ≤ `--max-new` (default 6).
      Self-owned = a type declared anywhere under `--owned-src` (Core/
      Infrastructure/Presentation); Ryn/BCL types do not count. Target-typed
      `new()` is attributed via the assigned field/property type on the same
      line; an unattributable one is reported, not skipped.
  C4  the entry bridge is exactly one sync-over-async call in the root: the
      async startup phase is bridged once at the wiring layer, never inside the
      orchestration (ADR entry-bridge-two-phase). Zero or several are violations.
  C5  every self-owned type CONSTRUCTED in the root must be in the
      ROOT_CONSTRUCTIBLE inventory (direction gate; C3 caps volume, C5 caps
      direction). A NEW name is a violation — resolve via DI or register the
      construction in `*Registration.cs`; assembly-level additions update the
      inventory deliberately (+ ADR). `new X(…)` and the DI factory channel
      `…CreateInstance<X>(…)` both count as constructions.
  C6  sequencer shape: `Bootstrap/StartupSequence*.cs` (the orchestrator the
      wiring layer triggers; ADR composition-mechanism-container-assembly) must
      stay a sequencer, not a second root — every public `Run*` entry phase
      holds no control flow and no quoted text (raw lines checked, comments
      included), the files construct no owned type (both channels attributed by
      last segment; target-typed `new()` attributed via the declared owner,
      unattributable ones reported; SEQUENCER_VALUE_TYPES is empty — reopening
      it needs its own ADR), and no sync bridge may remain (it lives in the
      wiring layer; ADR entry-bridge-two-phase).
  C7  pre-Build assembly inventory: `Bootstrap/PreBuildRegistration*.cs` (the
      registration-layer home that took over the root's pre-Build assembly,
      ADR sequencer-resolution-injection) may construct only types listed in
      PREBUILD_CONSTRUCTIBLE. C3 counts the root set alone, so without C7 the
      moved constructions would sit outside every gate; here they stay a
      deliberate inventory (reopening it needs its own ADR).

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
DEFAULT_MAX_NEW = 6
DEFAULT_MAX_DOT_PARTIALS = 1

# Assembly surface (C1/C2): every file that carries assembly code, wherever it
# lives. ROOT_SET alone is the weight gate (F3/F4) — moving assembly into
# Bootstrap/ must not put it outside the direction gates (ADR
# compose-root-lifetime-and-config-single-source D6). Adding an entry here is a
# deliberate act: new file = gate change = FULL review.
ASSEMBLY_SURFACE_EXTRA = (
    "Bootstrap/PreBuildRegistration.cs",
    "Bootstrap/ShellResources.cs",
    "RynOptionsProfile.cs",
)

# C2 inventory, bound per file (path relative to --src). A name here asserts
# "this member is assembly/startup-head, not orchestration" — for THAT file only.
ALLOWED_METHODS = {
    "Program.cs": {"Main"},
    "DesktopAdapter.cs": {"Run", "AcquireSingleInstance", "StartProxy"},
    "DesktopAdapter.App.cs": {"BuildApp", "RegisterServices"},
    "Bootstrap/PreBuildRegistration.cs": {
        "CreatePreflight",
        "CreateUpdateAndTrayAssembly",
        "CreateRuntimeStarter",
        "CreateShellResources",
        "CreateSharedState",
    },
    "Bootstrap/ShellResources.cs": {"ShellResources", "Attach", "Track", "Dispose"},
    "RynOptionsProfile.cs": {"Apply"},
}

# Files directly under Bootstrap/ that are NOT assembly: sequencer, registration
# extensions, stage values and routers. A new file here must be declared (as
# domain code or as assembly surface) — otherwise the gate fails.
BOOTSTRAP_DOMAIN_FILES = {
    "BootstrapCommandRouter.cs",
    "BootstrapRegistration.cs",
    "BootstrapStateFrame.cs",
    "FirstBootUi.cs",
    "RunServicesRegistration.cs",
    "RuntimeResolutions.cs",
    "ShellMintEpochAdapter.cs",
    "StartupSequence.cs",
    "StartupSequence.Supervision.cs",
    "StartupStages.cs",
    "StartupUseCaseRegistration.cs",
}

# Self-owned types the wiring layer may construct. Frozen at step-3 of
# ADR composition-mechanism-container-assembly, then cut to six by ADR
# sequencer-resolution-injection (C3 ballast): bootstrap head, update/tray
# assembly and the runtime starter moved to `Bootstrap/PreBuildRegistration.cs`,
# which is not in the root set (C7 guards that file's constructions). Entries are
# bare type names; a name here asserts "this construction is assembly, not a new
# coupling vector". New features resolve via DI; additions update this set
# deliberately (+ ADR). Reference-shape params/locals stay R1 review scope.
ROOT_CONSTRUCTIBLE = {
    # Trigger itself (Program.cs).
    "DesktopAdapter",
    # Container-before head: three of the four shared singletons are constructed
    # here pre-Build (the fourth — RuntimeTimeouts — loads via fail-safe `Load`,
    # so it costs no `new`); the shell forwarder is consumed by the proxy start
    # before Build. Kept by design — ADR sequencer-resolution-injection.
    "UiLocale",
    "DesktopUiLocaleStore",
    "DshShellForward",
    # The Build product (Ryn returns the application; it cannot be resolved).
    "AppSetup",
    # The single trigger construction, through the DI factory channel
    # (`ActivatorUtilities.CreateInstance<Bootstrap.StartupSequence>`): the wiring
    # layer assembles the orchestrator once. C3/C5 count that channel like `new`.
    "StartupSequence",
}

# Sequencer (C6) inventory: empty — the sequencer constructs no owned type at all
# (stage values ride locals/`Task`; ADR sequencer-stage-value-retirement). Reopening
# this set re-opens a construction channel in the sequencer and needs its own ADR.
SEQUENCER_VALUE_TYPES: set[str] = set()
# Zero: the sole sync bridge sits in the wiring layer as the two-phase entry
# (ADR entry-bridge-two-phase) — the sequencer's async phase is awaited by the
# root, so nothing may block inside the orchestration.
SEQUENCER_SYNC_BRIDGE_CAP = 0
SEQUENCER_GLOB = "StartupSequence*.cs"

# C4 expects exactly one entry bridge: `RunStartupAsync()` (I/O phase) is bridged
# once in the wiring layer, then the thread-0 sync phase runs. Zero would mean the
# phase split moved elsewhere, several would mean the bridge crept back inward
# (ADR entry-bridge-two-phase) — either way the deliberate inventory is re-decided.
ROOT_ENTRY_BRIDGES = 1

# Pre-Build assembly inventory (C7): `PreBuildRegistration*.cs` is the home that
# took over the root's pre-Build assembly (ADR sequencer-resolution-injection).
# C3 counts the root set only, so these constructions would otherwise be outside
# every gate. They stay a deliberate inventory; reopening needs its own ADR.
# DI-resolved services are absent by construction; listed entries are either `new`
# sites (including the DI factory channel) or assembly-time constructions none of
# the channels can see (today: static factories — reasons inline in the entry).
PREBUILD_GLOB = "PreBuildRegistration*.cs"
PREBUILD_CONSTRUCTIBLE = {
    # CaptionBarOptions：装配期经静态工厂 `CaptionBarOptions.Load(...)` 构造（自绘标题栏参数家）。
    # 在册的理由是「它确实在装配面被构造」——闸的三条通道（new／CreateInstance／target-typed new）
    # 看不见静态工厂形态，属闸的盲区（待办区 C7 缺口①），不是「该类型不在装配面」。
    "CaptionBarOptions",
    "CloseBehaviorPreference",
    "CloseGate",
    "DesktopUiLocaleStore",
    "DshShellForward",
    "FirstBootBootstrapService",
    "FirstBootUi",
    # OsLocaleProbe：UiLocale 的宿主 locale 探测端口实现（Core 读环境变量下沉后的接线点，
    # ADR architecture/2026-10-20-core-purity-boundaries）——共享态在容器之前构造，故只能在此 new。
    "OsLocaleProbe",
    # 自更新栈的三个薄适配器：拆自原单型适配器 UpdateStackAdapter（ADR
    # architecture/2026-10-20-update-stack-adapter-split），装配期构造故只能在此 new；
    # 安装器端口由 UpdateInstaller 直接实现（同一 ADR），不另设转发壳。
    "PackageDownloaderAdapter",
    "PagePump",
    # WindowScriptTarget：窗口脚本求值端口的 Ryn 适配器（ADR
    # architecture/2026-10-21-window-script-target-port）——共享态在容器之前构造，访问器以提供者惰性供给。
    "WindowScriptTarget",
    "Preflight",
    "ReleaseFeedAdapter",
    "RuntimeStarter",
    "SharedState",
    "ShellResources",
    "TrayController",
    "UiLocale",
    "UpdateCoordinator",
    "UpdateEnvironmentAdapter",
    "UpdateInstaller",
    "UpdateSetup",
}

# `Run()` must read as a call list: no branches/loops/exception structure.
_SEQUENCER_CONTROL_RE = re.compile(
    r"\b(?:if|else|for|foreach|while|do|switch|case|try|catch|finally|throw|"
    r"using|lock|goto|yield)\b")
# Every public `Run*` method in the sequencer is an entry phase whose body must
# read as a straight call list (ADR entry-bridge-two-phase splits `Run` into the
# async I/O phase and the thread-0 sync phase). Modifier prefixes (`static`,
# `async`, …) are matched so a rewritten signature cannot dodge the scan.
_RUN_SIGNATURE_RE = re.compile(
    r"^\s*public\s+(?:(?:static|async|sealed|override|virtual|partial|extern|unsafe|new)\s+)*"
    r"[\w<>.,\[\]]+\s+(Run\w*)\s*\(")

# Constructions: `new X(` / dotted `new Ns.X(` / factory `…CreateInstance<X>(…)`, all
# attributed by last segment (deeper generic nesting is a documented skip).
_NEW_RE = re.compile(r"\bnew\s+([\w.]+)(?:<[^()<>]*>)?\s*\(")
_CREATE_INSTANCE_RE = re.compile(r"\b(?:ActivatorUtilities\s*\.\s*)?CreateInstance<([\w.]+)>")
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
                  and (p.name == "Program.cs" or p.name.startswith("DesktopAdapter")))


def _assembly_files(src: Path) -> list[Path]:
    """C1/C2 scope: the root set plus the declared assembly-surface files."""
    files = list(_root_files(src))
    for rel in ASSEMBLY_SURFACE_EXTRA:
        path = src / rel
        if path.is_file():
            files.append(path)
    return sorted(files)


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


def _constructed_names(code: str) -> list[str]:
    """Owned-type construction candidates on one cleaned line: `new`/dotted
    `new`/the DI factory channel, all attributed by the last segment."""
    return [m.group(1).split(".")[-1]
            for pattern in (_NEW_RE, _CREATE_INSTANCE_RE)
            for m in pattern.finditer(code)]


def _sequencer_files(src: Path) -> list[Path]:
    bootstrap = src / "Bootstrap"
    if not bootstrap.is_dir():
        return []
    return sorted(bootstrap.glob(SEQUENCER_GLOB))


def _run_spans(cleaned: list[str]) -> list[tuple[int, int]]:
    """Line spans of every public `Run*` entry method body in cleaned lines.

    The span ends at the body's closing brace, at the arrow of an expression body
    (`=> …`), or at a bodiless declaration's `;` — never by running into the next
    member (an expression-bodied entry must not inherit a following method's
    control flow or quoted text).
    """
    spans: list[tuple[int, int]] = []
    for start, code in enumerate(cleaned):
        if not _RUN_SIGNATURE_RE.match(code):
            continue
        depth = 0
        opened = False
        end = start
        for i in range(start, len(cleaned)):
            line = cleaned[i]
            end = i
            if not opened:
                if "{" not in line and "=>" in line:
                    break  # expression body: the arrow line ends the scope
                if "{" in line:
                    opened = True
            depth += line.count("{") - line.count("}")
            if opened and depth <= 0:
                break
            if not opened and line.rstrip().endswith(";"):
                break  # bodiless declaration
        spans.append((start, end))
    return spans


def _sequencer_violations(src: Path, owned: set[str]) -> list[str]:
    out: list[str] = []
    files = _sequencer_files(src)
    if not files:
        return [f"  C6 no sequencer files found under {src / 'Bootstrap'} ({SEQUENCER_GLOB})"]
    for path in files:
        raws = path.read_text(encoding="utf-8").splitlines()
        cleaned = list(CSharpLineScanner(raws).iter_cleaned())
        for code in cleaned:
            for constructed in _constructed_names(code):
                if constructed in owned and constructed not in SEQUENCER_VALUE_TYPES:
                    out.append(
                        f"  C6 {path.name}: sequencer constructs `{constructed}` (resolve via the"
                        " container or *Registration.cs; reopening the stage-value whitelist needs its own ADR)")
            if _TARGET_NEW_RE.search(code):
                owner = TARGET_OWNER_RE.search(code)
                attributed = owner.group(1).split(".")[-1] if owner else None
                if attributed in owned and attributed not in SEQUENCER_VALUE_TYPES:
                    out.append(
                        f"  C6 {path.name}: sequencer constructs `{attributed}` (target-typed `new()`)")
                elif owner is None:
                    out.append(
                        f"  C6 {path.name}: unattributable target-typed `new()` "
                        "(attribute the declared type or write it explicitly)")
        bridges = sum(len(_GETAWAITER_RE.findall(code)) for code in cleaned)
        if bridges > SEQUENCER_SYNC_BRIDGE_CAP:
            out.append(
                f"  C6 {path.name}: {bridges} sync-over-async bridges > {SEQUENCER_SYNC_BRIDGE_CAP}"
                " (the entry bridge lives in the wiring layer; ADR entry-bridge-two-phase)")

        for start, end in _run_spans(cleaned):
            entry = _RUN_SIGNATURE_RE.match(cleaned[start]).group(1)
            for code in cleaned[start:end + 1]:
                m = _SEQUENCER_CONTROL_RE.search(code)
                if m:
                    out.append(
                        f"  C6 {path.name}: {entry}() holds control flow `{m.group(0)}`"
                        " (a straight call list; branches belong in a use case)")
            for line in raws[start:end + 1]:
                if '"' in line:
                    out.append(
                        f"  C6 {path.name}: {entry}() holds quoted text"
                        " (domain text belongs in a use case)")
                    break
    return out


def _prebuild_files(src: Path) -> list[Path]:
    bootstrap = src / "Bootstrap"
    if not bootstrap.is_dir():
        return []
    return sorted(bootstrap.glob(PREBUILD_GLOB))


def _prebuild_violations(src: Path, owned: set[str]) -> list[str]:
    """C7: constructions in the pre-Build assembly home must be inventoried.

    Both channels (`new` / DI factory, target-typed `new()`) count; the message
    mirrors C5's remedy (resolve via DI or update the inventory deliberately).
    """
    out: list[str] = []
    for path in _prebuild_files(src):
        raws = path.read_text(encoding="utf-8").splitlines()
        for code in CSharpLineScanner(raws).iter_cleaned():
            for constructed in _constructed_names(code):
                if constructed in owned and constructed not in PREBUILD_CONSTRUCTIBLE:
                    out.append(
                        f"  C7 {path.name}: pre-Build assembly constructs `{constructed}`"
                        " (assembly moved here is a deliberate inventory — resolve via DI or"
                        " update PREBUILD_CONSTRUCTIBLE + ADR)")
            if _TARGET_NEW_RE.search(code):
                owner = TARGET_OWNER_RE.search(code)
                attributed = owner.group(1).split(".")[-1] if owner else None
                if attributed in owned and attributed not in PREBUILD_CONSTRUCTIBLE:
                    out.append(
                        f"  C7 {path.name}: pre-Build assembly constructs `{attributed}`"
                        " (target-typed `new()`; add it to PREBUILD_CONSTRUCTIBLE deliberately)")
                elif owner is None:
                    out.append(
                        f"  C7 {path.name}: unattributable target-typed `new()` "
                        "(attribute the declared type or write it explicitly)")
    return out


def _violations(src: Path, owned_srcs: list[Path],
                max_new: int, max_dot_partials: int) -> tuple[list[str], int]:
    files = _root_files(src)
    if not files:
        return ([f"  compose-root set is empty under {src} (Program.cs / DesktopAdapter*.cs)"], 0)

    out: list[str] = []
    assembly = _assembly_files(src)
    dot_partials = [p.name for p in assembly if p.name != "Program.cs" and "." in p.stem]
    if len(dot_partials) > max_dot_partials:
        out.append(
            f"  C1 {len(dot_partials)} dot partials > {max_dot_partials}: {', '.join(dot_partials)}"
            " (orchestration belongs in Bootstrap/StartupSequence or a domain service)")

    bootstrap = src / "Bootstrap"
    if bootstrap.is_dir():
        declared = set(BOOTSTRAP_DOMAIN_FILES) | {Path(rel).name for rel in ASSEMBLY_SURFACE_EXTRA}
        for path in sorted(bootstrap.glob("*.cs")):
            if path.name not in declared:
                out.append(
                    f"  C8 Bootstrap/{path.name}: new file is neither domain nor assembly surface"
                    " (declare it in BOOTSTRAP_DOMAIN_FILES or ASSEMBLY_SURFACE_EXTRA —"
                    " moving assembly here must not escape the gates)")

    owned = _owned_types(owned_srcs)
    out.extend(_sequencer_violations(src, owned))
    out.extend(_prebuild_violations(src, owned))
    new_count = 0
    bridges = 0
    # C1/C2 覆盖装配面（根集 + 声明的装配文件）；C3/C4/C5 只覆盖根集（重量与自造面）。
    for path in assembly:
        is_root = path in files
        raws = path.read_text(encoding="utf-8").splitlines()
        for code in CSharpLineScanner(raws).iter_cleaned():
            for constructed in _constructed_names(code):
                if is_root and constructed in owned:
                    new_count += 1
                    if constructed not in ROOT_CONSTRUCTIBLE:
                        out.append(
                            f"  C5 {path.name}: root constructs unlisted type `{constructed}`"
                            " (resolve via DI or register the construction in *Registration.cs; "
                            "assembly-level additions update ROOT_CONSTRUCTIBLE deliberately)")
            if _TARGET_NEW_RE.search(code):
                owner = TARGET_OWNER_RE.search(code)
                attributed = owner.group(1).split(".")[-1] if owner else None
                if is_root and attributed and attributed in owned:
                    new_count += 1
                    if attributed not in ROOT_CONSTRUCTIBLE:
                        out.append(
                            f"  C5 {path.name}: root constructs unlisted type `{attributed}`"
                            " (target-typed `new()`; resolve via DI or update ROOT_CONSTRUCTIBLE deliberately)")
                elif is_root and owner is None:
                    out.append(
                        f"  C3 {path.name}: unattributable target-typed `new()` "
                        "(attribute the declared type or write it explicitly)")
            if is_root and _GETAWAITER_RE.search(code):
                bridges += len(_GETAWAITER_RE.findall(code))
            matched = None
            for pattern in (_COMPOSE_METHOD_RE, _COMPOSE_CTOR_RE, _COMPOSE_PROPERTY_RE):
                m = pattern.match(code)
                if m:
                    matched = m.group(1)
                    break
            allowed = ALLOWED_METHODS.get(path.relative_to(src).as_posix(), set())
            if matched and matched not in allowed:
                out.append(
                    f"  C2 {path.name}: assembly member `{matched}` is not in this file's inventory"
                    " (move it to a domain service; assembly additions update ALLOWED_METHODS"
                    " for that file deliberately)")

    if new_count > max_new:
        out.append(f"  C3 root `new` of self-owned types {new_count} > {max_new}")
    if bridges != ROOT_ENTRY_BRIDGES:
        out.append(
            f"  C4 root holds {bridges} sync-over-async bridges != {ROOT_ENTRY_BRIDGES}"
            " (the two-phase entry bridges the async startup phase exactly once at the"
            " wiring layer; ADR entry-bridge-two-phase)")
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

        # Passing fixture: inventory methods, few news, one dot partial, and the
        # single entry bridge the two-phase wiring layer is expected to hold.
        (root / "Program.cs").write_text(base_program(), encoding="utf-8")
        (root / "DesktopAdapter.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopAdapter
{
    private ShellEntry _entry = new ShellEntry();
    public int Run() { _entry.Start().GetAwaiter().GetResult(); return _entry.Run(); }
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
        (root / "DesktopAdapter.App.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopAdapter
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
        (root / "DesktopAdapter.App.cs").unlink()

        # C2: tuple-returning methods resolve to the method name, not `static`
        # (2b ResolveSharedState returns a value tuple; the return parens must
        # not shadow the method name).
        (root / "DesktopAdapter.App.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopAdapter
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
        # C2: generic-heavy tuple returns (Func<…> has no parens) stay named —
        # R2S2 robustness note verified: only nested-PAREN returns would fall
        # back, and none exist in the tree.
        (root / "DesktopAdapter.App.cs").write_text(
            """namespace Shell;
using System;
public sealed partial class DesktopAdapter
{
    private static (Func<int> F, int G) MakeFn() => (null, 1);
}
""", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C2 " in r and "MakeFn" in r for r in rows):
            print("  ok: C2 generic tuple return flagged by name")
        else:
            print(f"  ✗ C2 generic tuple return not named: {rows}")
            failed = 1
        (root / "DesktopAdapter.App.cs").unlink()

        # C1: two dot partials exceed the cap of one.
        (root / "DesktopAdapter.App.cs").write_text(
            "namespace Shell;\npublic sealed partial class DesktopAdapter\n{\n}\n", encoding="utf-8")
        (root / "DesktopAdapter.Extra.cs").write_text(
            "namespace Shell;\npublic sealed partial class DesktopAdapter\n{\n}\n", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C1 " in r for r in rows):
            print("  ok: C1 extra dot partial flagged")
        else:
            print(f"  ✗ C1 not flagged: {rows}")
            failed = 1
        (root / "DesktopAdapter.App.cs").unlink()
        (root / "DesktopAdapter.Extra.cs").unlink()

        # C3: new-of-owned over the cap (fixture cap = 1; ShellEntry twice).
        (domain / "ShellEntry.cs").write_text(
            "namespace Shell.Bootstrap;\npublic sealed class ShellEntry\n{\n}\n", encoding="utf-8")
        (root / "DesktopAdapter.C3.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopAdapter
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
        # C5: the same unlisted construction is flagged by type name (direction,
        # not just volume — a single `new Foo()` of a new type trips C5).
        if any(r.startswith("  C5 ") and "`ShellEntry`" in r for r in rows):
            print("  ok: C5 unlisted constructed type flagged by name")
        else:
            print(f"  ✗ C5 not flagged: {rows}")
            failed = 1
        # C5/B1: dotted `new Whatever.ShellEntry()` attributes by the last segment,
        # and the DI factory channel (`CreateInstance<T>`) counts like `new` — both
        # must trip C5, so rewriting a construction site cannot bypass the inventory.
        # 上一夹具的同类文件先清，计数以增量锚定（跨夹具残留的 ShellEntry 构造在 before 里）。
        (root / "DesktopAdapter.C3.cs").unlink()
        _, before = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        (root / "DesktopAdapter.C3.cs").write_text(
            "namespace Shell;\npublic sealed partial class DesktopAdapter\n{\n"
            "    public int Resolve() { var e = new Whatever.ShellEntry();"
            " var f = ActivatorUtilities.CreateInstance<ShellEntry>(); return 0; }\n}\n",
            encoding="utf-8")
        rows, after = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        flagged = [r for r in rows if "DesktopAdapter.C3.cs" in r and "C5 " in r]
        if after == before + 2 and len(flagged) == 2:
            print("  ok: C5 dotted + DI-factory constructions both flagged")
        else:
            print(f"  ✗ C5 channels not both flagged (Δ={after - before}, rows={rows})")
            failed = 1
        (root / "DesktopAdapter.C3.cs").unlink()

        # C3 (target-typed): `Type field = new();` counts via the declared type;
        # an unattributable `new()` is reported, not skipped.
        # 基线计数取自跨夹具残留（Program/DesktopAdapter 的 ShellEntry 构造）——
        # 断言以增量锚定，避免夹具间耦合读数漂移。
        _, base_count = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        (root / "DesktopAdapter.TN.cs").write_text(
            """
namespace Shell;
public sealed partial class DesktopAdapter
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
        (root / "DesktopAdapter.TN.cs").unlink()

        # C4: the entry bridge is exactly one (ADR entry-bridge-two-phase) — the
        # base fixture already holds one and passes; a second copy and a missing
        # bridge are both flagged.
        (root / "DesktopAdapter.C4.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopAdapter
{
    private void Boot() { var x = Wait().GetAwaiter().GetResult(); }
}
""", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C4 " in r for r in rows):
            print("  ok: C4 second sync-over-async bridge flagged")
        else:
            print(f"  ✗ C4 second bridge not flagged: {rows}")
            failed = 1
        (root / "DesktopAdapter.C4.cs").unlink()

        # C4: zero bridges moves the phase split out of the deliberate inventory.
        (root / "DesktopAdapter.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopAdapter
{
    private ShellEntry _entry = new ShellEntry();
    public int Run() { return _entry.Run(); }
    private void StartProxy() { }
}
""", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C4 " in r for r in rows):
            print("  ok: C4 missing entry bridge flagged")
        else:
            print(f"  ✗ C4 zero-bridge root not flagged: {rows}")
            failed = 1
        (root / "DesktopAdapter.cs").write_text(
            """namespace Shell;
public sealed partial class DesktopAdapter
{
    private ShellEntry _entry = new ShellEntry();
    public int Run() { _entry.Start().GetAwaiter().GetResult(); return _entry.Run(); }
    private void StartProxy() { }
}
""", encoding="utf-8")

        # C6: entry control flow/quoted text (every public `Run*` phase), constructions
        # (the stage-value whitelist is empty, so ANY owned construction in the
        # sequencer is a violation), the empty sync-bridge allowance (the entry
        # bridge lives in the wiring layer, ADR entry-bridge-two-phase), and the
        # empty sequencer set.
        (domain / "StageValue.cs").write_text(
            "namespace Shell.Bootstrap;\ninternal readonly record struct StageValue(int A);\n",
            encoding="utf-8")
        c6_fixtures = [
            ("control flow", "int Run() { if (_ready) { return 1; } return 0; }", "control flow"),
            ("quoted text", 'int Run() { var s = "x"; return 0; }', "quoted text"),
            ("service construction", "int Run() { var e = new ShellEntry(); return 0; }", "`ShellEntry`"),
            ("unattributable target-typed new", "int Run() { var x = F(new()); return 0; }",
             "unattributable target-typed"),
            ("sync-bridge cap",
             "int Run() { _ = P().GetAwaiter().GetResult(); return 0; }",
             "sync-over-async bridges"),
            # The entry is two phase methods now (ADR entry-bridge-two-phase):
            # each public `Run*` body is scanned, not just `Run` — and a modifier
            # prefix does not dodge the scan.
            ("async phase control flow",
             "async Task RunStartupAsync() { if (_ready) { await P(); } }", "control flow"),
            ("sync phase quoted text",
             'int RunMainLoop() { var s = "x"; return 0; }', "quoted text"),
            ("static modifier control flow",
             "static int Run() { if (_ready) { return 1; } return 0; }", "control flow"),
        ]
        for label, run, expected in c6_fixtures:
            (domain / "StartupSequence.cs").write_text(
                "namespace Shell.Bootstrap;\ninternal sealed class StartupSequence\n{\n"
                f"    public {run}\n}}\n", encoding="utf-8")
            rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
            hits = [r for r in rows if "C6 " in r]
            confirmed = (not hits) if expected is None else any(expected in r for r in hits)
            if confirmed:
                print(f"  ok: C6 {label}")
            else:
                print(f"  ✗ C6 {label} not confirmed: {hits}")
                failed = 1

        # C6: an expression-bodied entry ends at its arrow — it must not inherit
        # the next member's body (the old brace walk did, mis-attributing that
        # method's control flow to the entry).
        (domain / "StartupSequence.cs").write_text(
            "namespace Shell.Bootstrap;\ninternal sealed class StartupSequence\n{\n"
            "    public int RunMainLoop() => Helper();\n"
            "    private int Helper() { if (_ready) { return 1; } return 0; }\n}\n",
            encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if not any("C6 " in r and "control flow" in r for r in rows):
            print("  ok: C6 expression-bodied entry does not swallow the next member")
        else:
            print(f"  ✗ C6 expression-body scope wrong: {rows}")
            failed = 1

        # C6: the stage-value whitelist itself is empty (mandate of ADR
        # sequencer-stage-value-retirement) — a non-empty set would license a
        # construction channel inside the sequencer.
        if SEQUENCER_VALUE_TYPES:
            print(f"  ✗ C6 stage-value whitelist not empty: {sorted(SEQUENCER_VALUE_TYPES)}")
            failed = 1
        else:
            print("  ok: C6 stage-value whitelist empty")

        # C7: the pre-Build assembly home is a deliberate inventory (ADR
        # sequencer-resolution-injection) — C3 counts the root set only, so the
        # constructions moved there need this closure. An unlisted owned type is
        # flagged; the listed ones are not.
        (domain / "PreBuildRegistration.cs").write_text(
            "namespace Shell.Bootstrap;\ninternal static class PreBuildRegistration\n{\n"
            "    public static object Make() => new StageValue(1);\n}\n", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C7 " in r and "StageValue" in r for r in rows):
            print("  ok: C7 unlisted pre-Build construction flagged")
        else:
            print(f"  ✗ C7 not flagged: {rows}")
            failed = 1
        (domain / "PreBuildRegistration.cs").unlink()

        # C7: the unattributable target-typed channel is reported too (parity with
        # C5/C6) — a `new()` without a declared owner must not slip through silently.
        (domain / "PreBuildRegistration.cs").write_text(
            "namespace Shell.Bootstrap;\ninternal static class PreBuildRegistration\n{\n"
            "    public static object Make() => Wrap(new());\n"
            "    private static object Wrap(object value) => value;\n}\n", encoding="utf-8")
        rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS)
        if any("C7 " in r and "unattributable" in r for r in rows):
            print("  ok: C7 unattributable target-typed new flagged")
        else:
            print(f"  ✗ C7 unattributable not flagged: {rows}")
            failed = 1
        (domain / "PreBuildRegistration.cs").unlink()

        # C6: a source root without the sequencer set is reported, not skipped.
        missing = _sequencer_violations(Path(td) / "Nope", set())
        if any("C6 no sequencer files" in r for r in missing):
            print("  ok: C6 empty sequencer set flagged")
        else:
            print("  ✗ C6 empty sequencer set not flagged")
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
