#!/usr/bin/env python3
"""Verify assembly-surface semantics (architecture R1: the root set only assembles).

Complements the size gates (verify-code-health.py): F3 caps what the root set
WEIGHS in aggregate; this gate caps what the root HOLDS. The regression it
guards against (ADR compose-root-form-separation, replaying 1434271): stage
methods and business predicates accrete on the composition root as new partial
files, each dodging a per-file size budget.

Two scopes, declared in this file (the single home for these judgements):

  ROOT_SET       = `Program.cs` + `Hosting/AvaloniaHost.cs` (the trigger and the Avalonia
                   assembly head). Metrics: F3 weight budget (verify-code-health), F4,
                   C3, C5.  C4 (two-phase sync bridge) and C6 (sequencer shape) are
                   retired with the Ryn host they guarded (ADR avalonia-g5-ryn-host-retirement);
                   their numbering is kept so existing gate references stay stable.
  ASSEMBLY_SURFACE = ROOT_SET + ASSEMBLY_SURFACE_EXTRA (`Bootstrap/PreBuildRegistration.cs`,
                   `Bootstrap/ShellResources.cs`, `Hosting/ShellWiringRegistration.cs`). Direction
                   gates: C1 + C2 over the whole surface, C7 over
                   `PreBuildRegistration*.cs`, C8 over every first-level domain directory and over
                   the root directory itself (a file that is neither domain nor declared
                   surface fails; a declared surface file that vanished fails). Assembly code
                   must not escape the gates by moving out of the root files; adding a
                   file to ASSEMBLY_SURFACE_EXTRA is a gate change (FULL review) —
                   declaring a domain file is a data edit (LIGHT).

Six checks:

  C1  dot partials ≤ `--max-dot-partials` (default 1) across the assembly surface:
      each surface file is a single file, no dot-partial siblings.
  C2  every member declared in an assembly-surface file must be in that file's
      ALLOWED_METHODS entry (per-file binding: a name licensed in one file does not
      license it elsewhere). A NEW member is a violation — orchestration
      belongs in a domain service; a genuinely
      assembly-level method is added here deliberately (update inventory + ADR).
  C3  `new` of self-owned concrete types in the root ≤ `--max-new` (default 6).
      Self-owned = a type declared anywhere under `--owned-src` (Core/
      Infrastructure/Presentation); BCL types do not count. Target-typed
      `new()` is attributed via the assigned field/property type on the same
      line; an unattributable one is reported, not skipped.
  C5  every self-owned type CONSTRUCTED in the root must be in the
      ROOT_CONSTRUCTIBLE inventory (direction gate; C3 caps volume, C5 caps
      direction). A NEW name is a violation — resolve via DI or register the
      construction in `*Registration.cs`; assembly-level additions update the
      inventory deliberately (+ ADR). `new X(…)` and the DI factory channel
      `…CreateInstance<X>(…)` both count as constructions.
  C7  pre-Build assembly inventory: `Bootstrap/PreBuildRegistration*.cs` (the
      registration-layer home of the pre-Build assembly) may construct only types listed in
      PREBUILD_CONSTRUCTIBLE. Channels counted: `new` / DI factory (like C3),
      target-typed `new()`, and static factories whose return type is the type
      itself (`Foo.Load(...)` — the syntax channels cannot see those). The
      inventory is also checked for dead entries (listed but never constructed).
      C3 counts the root set alone, so without C7 the moved constructions would
      sit outside every gate; here they stay a deliberate inventory (reopening
      it needs its own ADR).
  C8  file inventory: every domain directory — a first-level subdirectory of
      `--src` holding `.cs` files (`bin`/`obj` excluded) — must be declared in
      the manifest
      (`scripts/domain-files.manifest.json`, `--domain-manifest`) together with
      its files. The check is two-way: an undeclared file, an undeclared
      directory, a declared file that no longer exists and a declared directory
      that no longer exists all fail — a rename or a deletion cannot drift the
      inventory silently, and a declared directory that lost every `.cs` still
      has its entries checked. Two classification rules sit beside it: every
      path in ASSEMBLY_SURFACE_EXTRA must exist (a vanished one is skipped
      silently by `_assembly_files`, so the file would leave every gate
      unwitnessed), and a root-level `.cs` file must be the root set or
      declared assembly surface — the root directory is the assembly surface,
      not a domain directory, and it carries no manifest key. Whether a file IS
      assembly surface remains a judgement recorded in ASSEMBLY_SURFACE_EXTRA;
      C8 asks whether that classification is recorded and unique. One file has
      one home: a file listed in both the manifest and ASSEMBLY_SURFACE_EXTRA
      fails.

Default is report-only (exit 0). `--enforce` exits 1 on any violation.
`--self-test` runs offline fixtures.

Usage: python3 scripts/verify-compose-root.py [--src DIR] [--enforce]
       python3 scripts/verify-compose-root.py --self-test
Exit code 0 = pass, 1 = violations.
"""

import argparse
import json
import re
import sys
from pathlib import Path

from gate_common import (CSharpLineScanner, STATIC_ACCESS_RE, TARGET_OWNER_RE, TYPE_DECL_RE,
                         is_compose_root_name)

DEFAULT_SRC = "src/DeepSeek.Harness.Desktop"
DEFAULT_OWNED_SRC = (
    "src/DeepSeek.Harness.Desktop",
    "src/DeepSeek.Harness.Desktop.Core",
    "src/DeepSeek.Harness.Desktop.Infrastructure",
)
DEFAULT_MAX_NEW = 6
DEFAULT_MAX_DOT_PARTIALS = 1
DEFAULT_DOMAIN_MANIFEST = Path(__file__).with_name("domain-files.manifest.json")

# Assembly surface (C1/C2): every file that carries assembly code, wherever it
# lives. ROOT_SET alone is the weight gate (F3/F4) — moving assembly into
# Bootstrap/ must not put it outside the direction gates (ADR
# compose-root-lifetime-and-config-single-source D6). Adding an entry here is a
# deliberate act: new file = gate change = FULL review.
ASSEMBLY_SURFACE_EXTRA = (
    "Bootstrap/PreBuildRegistration.cs",
    "Bootstrap/ShellResources.cs",
    # 宿主入口与运行期接线（G5 根集重绑，ADR avalonia-g5-ryn-host-retirement）：根集指
    # Program + AvaloniaHost；接线装配同属装配面，成员清单在 ALLOWED_METHODS。
    "Hosting/AvaloniaHost.cs",
    "Hosting/ShellWiringRegistration.cs",
)

# C2 inventory, bound per file (path relative to --src). A name here asserts
# "this member is assembly/startup-head, not orchestration" — for THAT file only.
ALLOWED_METHODS = {
    "Program.cs": {"Main"},
    "Bootstrap/PreBuildRegistration.cs": {
        "CreateStartupPrerequisites",
        "TryBecomePrimary",
        "CreatePreflight",
        "CreateRuntimeStarter",
        "CreateShellResources",
        "CreateSharedState",
    },
    "Bootstrap/ShellResources.cs": {"ShellResources", "Attach", "Track", "ReleaseLocalPorts", "Dispose"},
    "Hosting/AvaloniaHost.cs": {"Run", "AcquireSingleInstance", "AcquireRunMarker", "EnsureWebView2Runtime", "DisposeShell", "RunUi", "CreateStartup", "CreateLifetime"},
    "Hosting/ShellWiringRegistration.cs": {"Create", "CloseWindowOnUiThread", "RunWiring"},
}

# C8 domain inventory (C8 reads it; the criteria live in this file). The manifest
# is data, not judgement: it records that a file was classified, not what it is.
# Being assembly surface stays a judgement in ASSEMBLY_SURFACE_EXTRA above —
# editing that list is a gate change (FULL review), declaring a domain file is a
# manifest edit (data = LIGHT), so routine domain growth does not tax every batch
# with a full three-way review.
NON_DOMAIN_DIRS = ("bin", "obj")

# Self-owned types the assembly head may construct. Most pre-window assembly lives
# in `Bootstrap/PreBuildRegistration.cs` (C7 guards its constructions); what stays
# here is the host/recall/app/startup quartet the Avalonia lifetime needs before
# the main loop exists. Entries are bare type names; a name here asserts "this
# construction is assembly, not a new coupling vector". New features resolve via
# DI; additions update this set deliberately (+ ADR). Reference-shape params/locals
# stay R1 review scope.
ROOT_CONSTRUCTIBLE = {
    # 宿主持有者与唤回单点：装配头在容器之前构造（ADR avalonia-g0-entry-switch）。
    "HarnessRuntimeHost",
    "ShellRecall",
    # Avalonia 应用对象：生命周期回调需要它先于主循环存在，装配头唯一构造点。
    "ShellApplication",
    # 起步壳：dsh spawn 与铸币的后台任务载体，生命周期随本次 Run。
    "ShellStartup",
}

# Pre-Build assembly inventory (C7): `PreBuildRegistration*.cs` is the home that
# took over the root's pre-Build assembly (ADR sequencer-resolution-injection).
# C3 counts the root set only, so these constructions would otherwise be outside
# every gate. They stay a deliberate inventory; reopening needs its own ADR.
# DI-resolved services are absent by construction; listed entries are assembly-time
# constructions: `new` sites (including the DI factory channel), target-typed
# `new()`, or static factories (`Foo.Load(...)`; reasons inline where non-obvious).
# A listed name with no construction site in this file set is dead entry drift.
PREBUILD_GLOB = "PreBuildRegistration*.cs"
PREBUILD_CONSTRUCTIBLE = {
    # CaptionBarOptions：装配期经静态工厂 `CaptionBarOptions.Load(...)` 构造（自绘标题栏参数家）。
    "CaptionBarOptions",
    # 启动前置用例（G0 宿主迁移）：均 headless，spawn 前执行。
    "ProfileLifecycle",
    "CompanionPreSpawn",
    "DesktopUiLocaleStore",
    "DshShellForward",
    "FirstBootBootstrapService",
    "FirstBootUi",
    # LaunchOptions：启动期配置值的唯一解析点 `LaunchOptions.Resolve(...)`（静态工厂，
    # 不可变记录值；ADR composition-root-value-flow-pipeline 批次 3）。
    "LaunchOptions",
    # OsLocaleProbe：UiLocale 的宿主 locale 探测端口实现（Core 读环境变量下沉后的接线点，
    # ADR architecture/2026-10-20-core-purity-boundaries）——共享态在容器之前构造，故只能在此 new。
    "OsLocaleProbe",
    "PagePump",
    "Preflight",
    "RuntimeStarter",
    # RuntimeTimeouts：超时家经静态工厂 `RuntimeTimeouts.Load(...)` 构造，唯一 Load 点
    # （fail-safe 读配置；ADR 组合根寿命与配置单一来源 D4）。
    "RuntimeTimeouts",
    "SharedState",
    "ShellResources",
    "UiLocale",
}

# Constructions: `new X(` / dotted `new Ns.X(` / factory `…CreateInstance<X>(…)`, all
# attributed by last segment (deeper generic nesting is a documented skip).
_NEW_RE = re.compile(r"\bnew\s+([\w.]+)(?:<[^()<>]*>)?\s*\(")
_CREATE_INSTANCE_RE = re.compile(r"\b(?:ActivatorUtilities\s*\.\s*)?CreateInstance<([\w.]+)>")
# Static factory channel: `Foo.Load(...)` where a `static Foo Bar(...)` exists is a
# construction the syntax channels cannot see (C7 counts it; see _factory_returns).
# Derives from the shared access shape (single home: gate_common) plus the call paren.
_STATIC_CALL_RE = re.compile(STATIC_ACCESS_RE.pattern + r"\s*\(")
_STATIC_FACTORY_RE = re.compile(r"\bstatic\s+(?:async\s+)?([\w.<>\[\],?]+)\s+(\w+)\s*\(")
# `Type field = new();` — the assigned declared type gives target-typed new its owner.
_TARGET_NEW_RE = re.compile(r"\bnew\s*\(\)")
_COMPOSE_METHOD_RE = re.compile(r"^\s*(?:public|private|protected|internal)\s+(?:static\s+)?(?:\([^;{}]*\)\s+)?(?:[\w<>\[\],.?]+\s+)*(\w+)\s*\(")
# Constructors (single token between modifiers and paren) and expression-bodied
# members (`Type Name => ...`) both carry logic and belong in the C2 inventory.
_COMPOSE_CTOR_RE = re.compile(r"^\s*(?:public|private|protected|internal)\s+(\w+)\s*\(")
_COMPOSE_PROPERTY_RE = re.compile(r"^\s*(?:public|private|protected|internal)\s+[\w<>\[\],.?]+\s+(\w+)\s*=>")


def _assembly_identity() -> tuple[dict[str, set[str]], set[str]]:
    """The assembly-surface identity table: the declared surface files grouped
    by directory ("" for the root directory) and the root-level surface names.

    One decomposition of ASSEMBLY_SURFACE_EXTRA, so the inventory rule and the
    classification rule cannot drift apart.
    """
    by_dir: dict[str, set[str]] = {}
    root_files: set[str] = set()
    for rel in ASSEMBLY_SURFACE_EXTRA:
        path = Path(rel)
        parent = path.parent.as_posix()
        key = "" if parent == "." else parent
        by_dir.setdefault(key, set()).add(path.name)
        if key == "":
            root_files.add(path.name)
    return by_dir, root_files


def _root_files(src: Path) -> list[Path]:
    """Root-set files: membership by file name (the shared predicate), at any
    depth — a `Program.cs`/`AvaloniaHost*` name belongs to the root set
    wherever it sits, so C3/C5 and the F3 weight budget see the same set."""
    return sorted(p for p in src.rglob("*.cs")
                  if "obj" not in p.parts and "bin" not in p.parts
                  and is_compose_root_name(p.name))


def _assembly_files(src: Path) -> list[Path]:
    """C1/C2 scope: the root set plus the declared assembly-surface files and
    their dot-partial families.

    A surface file may be split into `Foo.Extra.cs` parts later; the
    declaration is by exact path, so a part would leave the direction gates
    unnoticed (C7 tracks `PreBuildRegistration*.cs` by glob, the surface
    declaration does not). Expanding the family keeps the part inside C1/C2:
    its assembly members then need an ALLOWED_METHODS entry of their own,
    which is a deliberate gate edit.
    """
    files = list(_root_files(src))
    for rel in ASSEMBLY_SURFACE_EXTRA:
        path = src / rel
        if not path.is_file():
            continue
        files.append(path)
        stem = path.stem.split(".")[0]
        files.extend(sibling for sibling in sorted(path.parent.glob(f"{stem}*.cs"))
                     if sibling != path)
    return sorted(dict.fromkeys(files))


def _cleaned_tree(owned_srcs: list[Path]):
    """`(lineno, cleaned_code)` for every source line under the owned trees."""
    for src in owned_srcs:
        for path in src.rglob("*.cs"):
            if any(part in ("obj", "bin") for part in path.relative_to(src).parts):
                continue
            raws = path.read_text(encoding="utf-8").splitlines()
            for lineno, code in enumerate(CSharpLineScanner(raws).iter_cleaned(), start=1):
                yield lineno, code


def _owned_types(lines: list[tuple[int, str]]) -> set[str]:
    """Type names declared anywhere in the owned trees (attribution面)."""
    return {m.group(1) for _, code in lines if (m := TYPE_DECL_RE.match(code))}


def _constructed_names(code: str) -> list[str]:
    """Owned-type construction candidates on one cleaned line: `new`/dotted
    `new`/the DI factory channel, all attributed by the last segment."""
    return [m.group(1).split(".")[-1]
            for pattern in (_NEW_RE, _CREATE_INSTANCE_RE)
            for m in pattern.finditer(code)]


def _factory_returns(lines: list[tuple[int, str]], owned: set[str]) -> dict[tuple[str, str], str]:
    """`(Type, Method) -> Type` for static methods whose return type is an owned type.

    Return type is matched by last segment, so `static Foo Bar(...)` anywhere in the
    tree makes `Foo.Bar(...)` a Foo construction site. Deliberately over-approximate:
    a helper that only *builds* a Foo still produces one.
    """
    found: dict[tuple[str, str], str] = {}
    for _, code in lines:
        m = _STATIC_FACTORY_RE.search(code)
        if not m:
            continue
        ret = m.group(1).split(".")[-1].split("<")[0]
        if ret in owned:
            found.setdefault((ret, m.group(2)), ret)
    return found


def _prebuild_files(src: Path) -> list[Path]:
    bootstrap = src / "Bootstrap"
    if not bootstrap.is_dir():
        return []
    return sorted(bootstrap.glob(PREBUILD_GLOB))


def _inventory_span() -> tuple[int, int]:
    """1-based line range of the PREBUILD_CONSTRUCTIBLE literal (0, 0 when absent)."""
    lines = Path(__file__).read_text(encoding="utf-8").splitlines()
    start = next((i for i, line in enumerate(lines)
                  if line.startswith("PREBUILD_CONSTRUCTIBLE")), None)
    if start is None:
        return 0, 0
    end = next((i for i, line in enumerate(lines[start:], start=start)
                if line.strip().startswith("}")), len(lines) - 1)
    return start + 1, end + 1


def _inventory_line(name: str) -> int:
    """Line of the `"<name>",` entry inside the PREBUILD_CONSTRUCTIBLE literal."""
    start, end = _inventory_span()
    if start == 0:
        return 0
    marker = f'"{name}",'
    for lineno, line in enumerate(
            Path(__file__).read_text(encoding="utf-8").splitlines()[start - 1:end],
            start=start):
        if line.strip().startswith(marker):
            return lineno
    return 0


def _prebuild_constructions(
        src: Path, owned: set[str], factories: dict[tuple[str, str], str],
        inventory: set[str] = PREBUILD_CONSTRUCTIBLE) -> list[str]:
    """C7: constructions in the pre-Build assembly home, and their violations.

    Channels: `new` / DI factory, target-typed `new()`, and static factories
    (`Foo.Load(...)`). Also reports inventory entries with no construction site
    (dead entry drift). The dead-entry check deliberately does **not** filter by
    the currently declared types: a type deleted or renamed by a refactor leaves
    a real dead entry, and that is the most common drift shape. Returns rows.
    """
    sites: dict[str, set[str]] = {}
    out: list[str] = []
    for path in _prebuild_files(src):
        raws = path.read_text(encoding="utf-8").splitlines()
        for lineno, code in enumerate(CSharpLineScanner(raws).iter_cleaned(), start=1):
            for constructed in _constructed_names(code):
                if constructed not in owned:
                    continue
                sites.setdefault(constructed, set()).add("new")
                if constructed not in inventory:
                    out.append(
                        f"  C7 {path.name}:{lineno}: pre-Build assembly constructs `{constructed}`"
                        " (assembly moved here is a deliberate inventory — resolve via DI or"
                        " update PREBUILD_CONSTRUCTIBLE + ADR)")
            if _TARGET_NEW_RE.search(code):
                owner = TARGET_OWNER_RE.search(code)
                attributed = owner.group(1).split(".")[-1] if owner else None
                if attributed in owned:
                    sites.setdefault(attributed, set()).add("target-typed new()")
                    if attributed not in inventory:
                        out.append(
                            f"  C7 {path.name}:{lineno}: pre-Build assembly constructs `{attributed}`"
                            " (target-typed `new()`; add it to PREBUILD_CONSTRUCTIBLE deliberately)")
                elif owner is None:
                    out.append(
                        f"  C7 {path.name}:{lineno}: unattributable target-typed `new()` "
                        "(attribute the declared type or write it explicitly)")
            for m in _STATIC_CALL_RE.finditer(code):
                constructed = factories.get((m.group(1), m.group(2)))
                if constructed is None:
                    continue
                sites.setdefault(constructed, set()).add(f"factory `{m.group(1)}.{m.group(2)}()`")
                if constructed not in inventory:
                    out.append(
                        f"  C7 {path.name}:{lineno}: pre-Build assembly constructs `{constructed}` via"
                        f" static factory `{m.group(1)}.{m.group(2)}()` (the syntax channels cannot"
                        " see it — resolve via DI or update PREBUILD_CONSTRUCTIBLE + ADR)")
    for name in sorted(set(inventory) - set(sites)):
        where = _inventory_line(name)
        at = f" (line {where})" if where else ""
        out.append(
            f"  C7 inventory lists `{name}`{at} but {PREBUILD_GLOB} never constructs it"
            " (dead entry — drop it, or the construction moved and the inventory must follow)")
    return out


def _load_domain_manifest(path: Path) -> dict[str, set[str]]:
    """C8 manifest (domain directory -> declared file names). An unreadable or
    malformed manifest is a hard error, never an empty inventory: an empty
    inventory would silently mean `no domain files exist`."""
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except OSError as exc:
        raise SystemExit(f"C8 domain manifest unreadable: {path} ({exc})") from exc
    except ValueError as exc:
        raise SystemExit(f"C8 domain manifest is not valid JSON: {path} ({exc})") from exc
    if not isinstance(raw, dict):
        raise SystemExit(f"C8 domain manifest must be an object: {path}")

    entries: dict[str, set[str]] = {}
    for name, files in raw.items():
        if name.startswith("_"):
            continue
        if not isinstance(files, list) or not all(isinstance(f, str) for f in files):
            raise SystemExit(f"C8 domain manifest entry `{name}` must be a list of file names")
        entries[name] = set(files)
    return entries


def _domain_inventory_violations(src: Path, manifest: dict[str, set[str]]) -> list[str]:
    """C8 over every domain directory under `src`, both directions (undeclared
    file, undeclared directory, dead file, dead directory)."""
    surface, _ = _assembly_identity()

    actual = {p.name: p for p in sorted(src.iterdir())
              if p.is_dir() and p.name not in NON_DOMAIN_DIRS}
    out: list[str] = []
    for name in sorted(actual):
        files = {p.name for p in actual[name].glob("*.cs")}
        declared = manifest.get(name)
        if declared is None:
            if not files:
                continue  # not a domain directory, nothing to declare
            out.append(
                f"  C8 {name}/: domain directory is not in the manifest"
                " (declare its files in scripts/domain-files.manifest.json)")
            continue
        surface_files = surface.get(name, set())
        for dup in sorted(declared & surface_files):
            out.append(
                f"  C8 {name}/{dup}: declared in both the manifest and ASSEMBLY_SURFACE_EXTRA"
                " (one file, one home)")
        # The root set is by name (any depth), so a manifest file carrying a
        # root-set name would be two owners at once: the strict C1-C5 scope
        # and the domain inventory.
        for owned in sorted(f for f in declared if is_compose_root_name(f)):
            out.append(
                f"  C8 {name}/{owned}: declared in both the manifest and the root set"
                " (one file, one home — the root set is by name)")
        domain_files = declared - surface_files
        for extra in sorted(files - domain_files - surface_files):
            out.append(
                f"  C8 {name}/{extra}: new file is neither domain nor assembly surface"
                " (declare it in scripts/domain-files.manifest.json, or in"
                " ASSEMBLY_SURFACE_EXTRA if it is assembly code — moving assembly here"
                " must not escape the gates)")
        # Dead entries: a manifest file that no longer exists. Assembly-surface
        # paths live in the script, not in the manifest (one file, one home);
        # their existence is checked in `_assembly_surface_violations`.
        for gone in sorted(domain_files - files):
            out.append(
                f"  C8 {name}/{gone}: inventory lists a file that does not exist"
                " (dead entry — drop it, or the file moved and the inventory must follow)")
    for name in sorted(set(manifest) - set(actual)):
        out.append(
            f"  C8 {name}/: inventory lists a directory that does not exist"
            " (dead entry — drop it)")
    return out


def _assembly_surface_violations(src: Path) -> list[str]:
    """C8, the two classification rules the domain inventory cannot express: a
    declared assembly-surface file must exist, and a root-level file must be
    the root set or declared assembly surface.

    A vanished assembly-surface path is skipped silently by `_assembly_files`
    (`if path.is_file()`), so without this check the file leaves every gate
    without a word. A root-level file outside both sets is outside every gate
    as well: the root directory is the assembly surface, not a domain
    directory, and it had no inventory face at all.
    """
    out: list[str] = []
    _, root_surface = _assembly_identity()
    for rel in ASSEMBLY_SURFACE_EXTRA:
        if not (src / rel).is_file():
            out.append(
                f"  C8 assembly surface {rel}: declared in ASSEMBLY_SURFACE_EXTRA but the file"
                " does not exist (drop it, or the file moved and the declaration must follow)")
    for path in sorted(src.glob("*.cs")):
        if is_compose_root_name(path.name) or path.name in root_surface:
            continue
        out.append(
            f"  C8 {path.name}: root-level file is neither root set nor assembly surface"
            " (move it into a domain directory, or declare it in ASSEMBLY_SURFACE_EXTRA"
            " if it is assembly code)")
    return out


def _violations(src: Path, owned_srcs: list[Path], max_new: int, max_dot_partials: int,
                inventory: set[str] | None = None) -> tuple[list[str], int]:
    files = _root_files(src)
    if not files:
        return ([f"  compose-root set is empty under {src} (Program.cs / AvaloniaHost*.cs)"], 0)

    out: list[str] = []
    assembly = _assembly_files(src)
    dot_partials = [p.name for p in assembly if p.name != "Program.cs" and "." in p.stem]
    if len(dot_partials) > max_dot_partials:
        out.append(
            f"  C1 {len(dot_partials)} dot partials > {max_dot_partials}: {', '.join(dot_partials)}"
            " (assembly head plus one dot partial; deeper splits belong in a domain service)")

    owned_lines = list(_cleaned_tree(owned_srcs))
    owned = _owned_types(owned_lines)
    prebuild_rows = _prebuild_constructions(
        src, owned, _factory_returns(owned_lines, owned),
        PREBUILD_CONSTRUCTIBLE if inventory is None else inventory)
    out.extend(prebuild_rows)
    new_count = 0
    # C1/C2 覆盖装配面（根集 + 声明的装配文件）；C3/C5 只覆盖根集（重量与自造面）。
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
    return out, new_count


def _self_test() -> int:
    import tempfile

    failed = 0
    saved_allowed = dict(ALLOWED_METHODS)
    ALLOWED_METHODS.clear()
    ALLOWED_METHODS.update({
        "Program.cs": {"Main"},
        "Hosting/AvaloniaHost.cs": {"Run", "RunUi"},
    })

    def check(label: str, ok: bool, rows: list[str] | None = None) -> None:
        nonlocal failed
        if ok:
            print(f"  ok: {label}")
        else:
            print(f"  ✗ {label}: {rows}")
            failed = 1

    try:
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "Shell"
            root.mkdir(parents=True)
            domain = root / "Bootstrap"
            domain.mkdir()
            (root / "Program.cs").write_text(
                "namespace Shell;\npublic static class Program\n{\n"
                "    public static int Main(string[] args) => 0;\n}\n", encoding="utf-8")
            (root / "Hosting").mkdir()
            (root / "Hosting" / "AvaloniaHost.cs").write_text(
                "namespace Shell.Hosting;\ninternal static class AvaloniaHost\n{\n"
                "    public static int Run() => 0;\n    public static int RunUi() => 0;\n}\n",
                encoding="utf-8")
            (domain / "ShellEntry.cs").write_text(
                "namespace Shell.Bootstrap;\npublic sealed class ShellEntry\n{\n}\n", encoding="utf-8")

            rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            check("conforming assembly head -> pass", not rows, rows)

            # C2: an assembly member outside the per-file inventory.
            (root / "AvaloniaHost.App.cs").write_text(
                "namespace Shell;\ninternal static partial class AvaloniaHost\n{\n"
                "    private static void EnsureDesktopProfile() { }\n}\n", encoding="utf-8")
            rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            check("C2 unknown assembly member flagged",
                  any("C2 " in r and "EnsureDesktopProfile" in r for r in rows), rows)
            (root / "AvaloniaHost.App.cs").unlink()

            # C1: more dot partials than the cap of one.
            (root / "AvaloniaHost.App.cs").write_text("namespace Shell;\ninternal static partial class AvaloniaHost\n{\n}\n", encoding="utf-8")
            (root / "AvaloniaHost.Extra.cs").write_text("namespace Shell;\ninternal static partial class AvaloniaHost\n{\n}\n", encoding="utf-8")
            rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            check("C1 extra dot partial flagged", any("C1 " in r for r in rows), rows)
            (root / "AvaloniaHost.App.cs").unlink()
            (root / "AvaloniaHost.Extra.cs").unlink()

            # C3/C5: owned-type constructions in the root are capped and inventoried.
            (root / "AvaloniaHost.C3.cs").write_text(
                "namespace Shell;\ninternal sealed class C3Fixture\n{\n"
                "    private ShellEntry _a = new ShellEntry();\n"
                "    private ShellEntry _b = new ShellEntry();\n}\n", encoding="utf-8")
            rows, count = _violations(root, [root], 1, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            check("C3 new-of-owned cap flagged", any(r.startswith("  C3 root `new`") for r in rows), rows)
            check("C5 unlisted constructed type flagged by name",
                  any(r.startswith("  C5 ") and "`ShellEntry`" in r for r in rows), rows)
            (root / "AvaloniaHost.C3.cs").unlink()

            # C5: dotted `new` and the DI factory channel both count.
            _, before = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            (root / "AvaloniaHost.C3.cs").write_text(
                "namespace Shell;\ninternal sealed class C3Fixture\n{\n"
                "    private object Make() => new Whatever.ShellEntry();\n"
                "    private object Build() => ActivatorUtilities.CreateInstance<ShellEntry>();\n}\n",
                encoding="utf-8")
            rows, after = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            flagged = [r for r in rows if "C5 " in r and "ShellEntry" in r]
            check("C5 dotted + DI-factory channels both flagged",
                  after == before + 2 and len(flagged) == 2, rows)
            (root / "AvaloniaHost.C3.cs").unlink()

            # C3/C5: target-typed `new()` is attributed by the declared type.
            (root / "AvaloniaHost.TN.cs").write_text(
                "namespace Shell;\ninternal sealed class TargetFixture\n{\n"
                "    private ShellEntry _c = new();\n}\n", encoding="utf-8")
            rows, count = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            check("C5 target-typed new attributed and flagged",
                  any("C5 " in r and "ShellEntry" in r for r in rows), rows)
            (root / "AvaloniaHost.TN.cs").unlink()

            # C7: the pre-Build home is a deliberate inventory (new / static factory / dead entry).
            (domain / "StageValue.cs").write_text(
                "namespace Shell.Bootstrap;\ninternal readonly record struct StageValue(int A)\n{\n"
                "    public static StageValue Create() => new(1);\n}\n", encoding="utf-8")
            (domain / "PreBuildRegistration.cs").write_text(
                "namespace Shell.Bootstrap;\ninternal static class PreBuildRegistration\n{\n"
                "    public static object Make() => new StageValue(1);\n"
                "    public static object Make2() => StageValue.Create();\n}\n", encoding="utf-8")
            rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS, inventory=set())
            check("C7 unlisted new + static-factory construction flagged",
                  any("C7 " in r and "StageValue" in r for r in rows), rows)
            (domain / "PreBuildRegistration.cs").write_text(
                "namespace Shell.Bootstrap;\ninternal static class PreBuildRegistration\n{\n"
                "    public static object Make() => new object();\n}\n", encoding="utf-8")
            rows, _ = _violations(root, [root], DEFAULT_MAX_NEW, DEFAULT_MAX_DOT_PARTIALS,
                                  inventory={"StageValue"})
            check("C7 dead inventory entry flagged", any("dead entry" in r and "StageValue" in r for r in rows), rows)
            (domain / "StageValue.cs").unlink()
            (domain / "PreBuildRegistration.cs").unlink()

            # C8: the domain-file inventory is two-way; root-level files are root set or surface.
            def c8(label: str, rows: list[str], needle: str | None = None) -> None:
                hit = (not rows) if needle is None else (len(rows) == 1 and needle in rows[0])
                check(f"C8 {label}", hit, rows)

            c8_src = Path(td) / "Domains"
            c8_dir = c8_src / "Bootstrap"
            c8_dir.mkdir(parents=True)
            (c8_dir / "Declared.cs").write_text("namespace D;\n", encoding="utf-8")
            c8_manifest = {"Bootstrap": {"Declared.cs"}}
            c8("conforming inventory -> pass", _domain_inventory_violations(c8_src, c8_manifest))
            (c8_dir / "AvaloniaHost.cs").write_text("namespace D;\n", encoding="utf-8")
            c8("manifest entry named like the root set flagged",
               _domain_inventory_violations(c8_src, {"Bootstrap": {"Declared.cs", "AvaloniaHost.cs"}}),
               "C8 Bootstrap/AvaloniaHost.cs: declared in both the manifest and the root set")
            (c8_dir / "AvaloniaHost.cs").unlink()
            (c8_dir / "Fresh.cs").write_text("namespace D;\n", encoding="utf-8")
            c8("undeclared domain file flagged", _domain_inventory_violations(c8_src, c8_manifest),
               "C8 Bootstrap/Fresh.cs: new file is neither domain")
            (c8_dir / "Fresh.cs").unlink()
            c8("dead file entry flagged",
               _domain_inventory_violations(c8_src, {"Bootstrap": {"Declared.cs", "Ghost.cs"}}),
               "C8 Bootstrap/Ghost.cs: inventory lists a file that does not exist")

            surf = Path(td) / "Surface"
            (surf / "Bootstrap").mkdir(parents=True)
            (surf / "Hosting").mkdir()
            (surf / "Program.cs").write_text("namespace D;\n", encoding="utf-8")
            (surf / "Hosting" / "AvaloniaHost.cs").write_text("namespace D;\n", encoding="utf-8")
            (surf / "Hosting" / "ShellWiringRegistration.cs").write_text("namespace D;\n", encoding="utf-8")
            (surf / "Bootstrap" / "PreBuildRegistration.cs").write_text("namespace D;\n", encoding="utf-8")
            (surf / "Bootstrap" / "ShellResources.cs").write_text("namespace D;\n", encoding="utf-8")
            c8("root set and declared surface pass", _assembly_surface_violations(surf))
            (surf / "Stray.cs").write_text("namespace D;\n", encoding="utf-8")
            c8("stray root-level file flagged", _assembly_surface_violations(surf),
               "C8 Stray.cs: root-level file is neither root set nor assembly surface")
            (surf / "Stray.cs").unlink()

            # One file, one home: a file listed in both the manifest and the surface is a violation.
            c8("double registration flagged",
               _domain_inventory_violations(c8_src, {"Bootstrap": {"Declared.cs", "PreBuildRegistration.cs"}}),
               "C8 Bootstrap/PreBuildRegistration.cs: declared in both the manifest and ASSEMBLY_SURFACE_EXTRA")

            # A declared directory that no longer exists.
            c8("dead directory entry flagged",
               _domain_inventory_violations(c8_src, {"Bootstrap": {"Declared.cs"}, "Nope": {"Also.cs"}}),
               "C8 Nope/: inventory lists a directory that does not exist")

            # A directory that exists but holds no .cs files keeps no live entries.
            (c8_src / "Emptied").mkdir()
            c8("emptied directory cannot hide dead entries",
               _domain_inventory_violations(c8_src, {"Bootstrap": {"Declared.cs"}, "Emptied": {"Was.cs"}}),
               "C8 Emptied/Was.cs: inventory lists a file that does not exist")
            (c8_src / "Emptied").rmdir()

            # A declared surface file that vanished from disk.
            (surf / "Bootstrap" / "ShellResources.cs").unlink()
            c8("vanished assembly-surface file flagged", _assembly_surface_violations(surf),
               "C8 assembly surface Bootstrap/ShellResources.cs: declared in ASSEMBLY_SURFACE_EXTRA"
               " but the file does not exist")
            (surf / "Bootstrap" / "ShellResources.cs").write_text("namespace D;\n", encoding="utf-8")

            # A dot partial of a declared surface file stays inside the C1/C2 scope.
            (surf / "Bootstrap" / "PreBuildRegistration.Extra.cs").write_text("namespace D;\n", encoding="utf-8")
            scope = {p.relative_to(surf).as_posix() for p in _assembly_files(surf)}
            check("C8 dot partial of a surface file stays in the C1/C2 scope",
                  "Bootstrap/PreBuildRegistration.Extra.cs" in scope)
            (surf / "Bootstrap" / "PreBuildRegistration.Extra.cs").unlink()

            # A malformed manifest is a hard error, not an empty inventory.
            for label, payload, expect in [
                ("malformed manifest", "{not json", "not valid JSON"),
                ("non-object manifest", "[1, 2]", "must be an object"),
                ("non-list entry", '{"Bootstrap": "A.cs"}', "must be a list of file names"),
            ]:
                bad = Path(td) / f"{label.replace(' ', '-')}.json"
                bad.write_text(payload, encoding="utf-8")
                try:
                    _load_domain_manifest(bad)
                except SystemExit as exc:
                    check(f"C8 {label} is a hard error", expect in str(exc), [str(exc)])
                else:
                    check(f"C8 {label} is a hard error", False)
    finally:
        ALLOWED_METHODS.clear()
        ALLOWED_METHODS.update(saved_allowed)

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
    parser.add_argument("--domain-manifest", default=str(DEFAULT_DOMAIN_MANIFEST),
                        help="C8 domain-file inventory (JSON: directory -> file names)")
    parser.add_argument("--enforce", action="store_true",
                        help="exit 1 on any violation (default: report only)")
    args = parser.parse_args()

    manifest = _load_domain_manifest(Path(args.domain_manifest))
    rows, new_count = _violations(Path(args.src), [Path(s) for s in args.owned_src],
                                  args.max_new, args.max_dot_partials)
    rows.extend(_domain_inventory_violations(Path(args.src), manifest))
    rows.extend(_assembly_surface_violations(Path(args.src)))
    print(f"  root new-of-owned count: {new_count} (cap {args.max_new})")
    print(f"  C7 inventory: {len(PREBUILD_CONSTRUCTIBLE)} entries")
    print(f"  C8 inventory: {sum(len(v) for v in manifest.values())} files in {len(manifest)} domain dirs")
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
