#!/usr/bin/env python3
"""Probe the installed dsh client assets for the upstream anchors this shell injects against.

Why this gate exists: the shell's chrome and banners hook upstream DOM class
suffixes (`[class*="sidebarCol"]`) and `--dsw-*` design tokens. Both move
constantly upstream (non-merge commits since 2026-06: 65 in the ui-layout client
tree, 24 in ui-theme's design-platform.css). A rename throws nothing — the page
just silently loses one visual rule, and only a user machine shows it. This gate
moves "found on a user's machine" back to "found before release / weekly".

Anchor list lives in scripts/upstream-anchors.json (single home for the anchors
in the register). Tiers:
  - required     : must exist on the probed install; missing = violation.
  - preview-only : ships on the preview line (alpha) ahead of the stable line
                   (next); missing is a `note:`, never a violation — the product
                   follows the stable channel (ADR process/2026-10-13-upstream-anchor-probe).

Presence rule is per token shape, not a bare substring:
  - `--` tokens (CSS custom properties) must appear in DEFINITION form (`--x:`),
    because a rename leaves `var(--x)` references behind in other bundles — a
    substring hit there would keep the gate green with the token gone.
  - every other token (class suffix, data-* name) is matched with a right-hand
    identifier boundary, so a longer neighbour (`sidebarCollapsed`,
    `headerActionsHidden`, `sidebarCol_extra`) cannot stand in for it.

Probe source is the *installed* @deepseek-ai/dsh package's own client assets
(`dsh-client-*/lib`, `dsh-web-frontend/dist`) — the same bytes `dsh web` serves
and this shell injects into, so no network and no tag guessing. Resolution: an
explicit `--dsh-root` is authoritative — if it is not a dsh package root the run
fails loud rather than silently probing some other install; otherwise the root is
auto-resolved from `npm root -g`, then from the realpath of `dsh` on PATH. No
install found = SKIP (exit 0): a contributor without a global dsh is not a gate hit.

The probe asserts token presence only. It does NOT re-verify selector semantics
(scoping, specificity, cascade) — that surface stays with visual/real-machine checks.

Default is report-only; `--enforce` exits 1 when a required anchor is missing.
An unprobeable install (no client assets under a resolved root) always exits 1:
"cannot tell" is not "passed".

Usage:
    python3 scripts/verify-upstream-anchors.py [--manifest P] [--dsh-root P] [--enforce]
    python3 scripts/verify-upstream-anchors.py --self-test
Exit code 0 = pass (or self-test passed), 1 = violations (or self-test failed).
"""

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT_MANIFEST = "scripts/upstream-anchors.json"
DSH_PACKAGE_NAME = "@deepseek-ai/dsh"
# Client assets are JS/CSS bundles. JSON/HTML are excluded on purpose: they carry
# no anchor evidence in the published layout and only widen the false-hit surface.
TEXT_SUFFIXES = frozenset({".js", ".mjs", ".cjs", ".css"})
MAX_FILE_BYTES = 4 * 1024 * 1024
TIERS = ("required", "preview-only")


def load_anchors(manifest: Path) -> tuple[list[dict], list[str]]:
    """Return (anchors, errors) for one manifest. Shape errors are violations."""
    if not manifest.is_file():
        return [], [f"{manifest}: manifest not found (the anchor list is the gate's input)"]
    try:
        data = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return [], [f"{manifest}: unreadable manifest: {exc}"]

    errors: list[str] = []
    anchors = data.get("anchors")
    if not isinstance(anchors, list) or not anchors:
        return [], [f"{manifest}: 'anchors' must be a non-empty list"]
    seen: set[str] = set()
    for i, entry in enumerate(anchors):
        for key in ("id", "token", "tier", "used_by", "impact"):
            if not isinstance(entry.get(key), str) or not entry[key]:
                errors.append(f"{manifest}: anchors[{i}] missing non-empty '{key}'")
        token = entry.get("token")
        if isinstance(token, str):
            if token in seen:
                errors.append(f"{manifest}: duplicate token '{token}'")
            seen.add(token)
        if entry.get("tier") not in TIERS:
            errors.append(f"{manifest}: anchors[{i}] tier '{entry.get('tier')}' not in {TIERS}")
    # A register whose every anchor is preview-only can never fail: one tier edit
    # would disarm the gate, so at least one anchor must sit on the stable channel.
    if anchors and not any(a.get("tier") == "required" for a in anchors):
        errors.append(f"{manifest}: no 'required' anchor — the register must assert at least "
                      "one anchor on the stable channel, else --enforce can never fail")
    return anchors, errors


def matcher(token: str) -> re.Pattern[str]:
    """Compile the presence rule for one token (see the module docstring).

    `--x` must show up in definition form; a rename leaves `var(--x)` behind.
    Anything else needs a right-hand identifier boundary so a longer name that
    merely starts with the token (`sidebarCollapsed`, `sidebarCol_extra`) cannot
    stand in for it. Hashed CSS-module classes put the hash on the LEFT
    (`pI_x6G_sidebarCol`), so only the right side needs the guard.
    """
    escaped = re.escape(token)
    if token.startswith("--"):
        return re.compile(escaped + r"[^A-Za-z0-9-]*:")
    return re.compile(escaped + r"(?![A-Za-z0-9_$])")


def _package_root(candidate: Path) -> Path | None:
    """Return the candidate when it is the dsh package root, else None."""
    try:
        data = json.loads((candidate / "package.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    return candidate if data.get("name") == DSH_PACKAGE_NAME else None


def resolve_auto_root() -> Path | None:
    """Auto-resolve the dsh package root: `npm root -g`, then `dsh` on PATH."""
    candidates: list[Path] = []
    try:
        out = subprocess.run(["npm", "root", "-g"], capture_output=True, text=True,
                             timeout=30, check=False)
        if out.returncode == 0 and out.stdout.strip():
            candidates.append(Path(out.stdout.strip()) / DSH_PACKAGE_NAME)
    except (OSError, subprocess.SubprocessError):
        pass

    binary = shutil.which("dsh")
    if binary:
        resolved = Path(binary).resolve()
        for parent in (resolved.parent, *resolved.parents):
            if (parent / "package.json").is_file():
                candidates.append(parent)
                break

    for c in candidates:
        found = _package_root(c)
        if found is not None:
            return found
    return None


def dsh_version(root: Path) -> str:
    """Package version of the probed install ("unknown" when unreadable)."""
    try:
        return str(json.loads((root / "package.json").read_text(encoding="utf-8"))
                   .get("version", "unknown"))
    except (OSError, json.JSONDecodeError):
        return "unknown"


def client_asset_files(root: Path) -> list[Path]:
    """Text assets of the client packages bundled with the install.

    Layout pinned by the published package: client code ships as
    `<root>/node_modules/@deepseek-ai/dsh-client-*/` plus the web entry's
    `dsh-web-frontend/dist`. The `node_modules` glob keeps a hoisted layout
    working too; unrelated dependency trees are never walked.
    """
    dirs: list[Path] = []
    for base in (root / "node_modules" / "@deepseek-ai", root / "node_modules"):
        if not base.is_dir():
            continue
        for pattern in ("dsh-client-*", "dsh-web-frontend"):
            dirs.extend(sorted(p for p in base.glob(pattern) if p.is_dir()))
    files: set[Path] = set()
    for d in dirs:
        for f in d.rglob("*"):
            if not f.is_file() or f.suffix not in TEXT_SUFFIXES:
                continue
            try:
                if f.stat().st_size <= MAX_FILE_BYTES:
                    files.add(f)
            except OSError:
                continue
    return sorted(files)


def probe(files: list[Path], anchors: list[dict]) -> dict[str, str | None]:
    """Map token -> first file matching its presence rule (None when absent).

    Each file is read once and checked against every still-unresolved token, so
    the scan stops as soon as all anchors are found.
    """
    hits: dict[str, str | None] = {a["token"]: None for a in anchors}
    pending = {a["token"]: matcher(a["token"]) for a in anchors}
    for f in files:
        if not pending:
            break
        try:
            text = f.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for token in sorted(pending):
            if pending[token].search(text):
                hits[token] = str(f)
                del pending[token]
    return hits


def classify(anchors: list[dict], hits: dict[str, str | None]) -> tuple[list[str], list[str], list[str]]:
    """Split probe results into (violations, notes, ok_lines)."""
    violations: list[str] = []
    notes: list[str] = []
    oks: list[str] = []
    for a in anchors:
        token, tier, hit = a["token"], a["tier"], hits.get(a["token"])
        if hit:
            oks.append(f"OK    {token:<36} {tier:<13} {hit}")
        elif tier == "required":
            violations.append(f"missing required anchor '{token}' ({a['id']}): "
                              f"{a['impact']} — 消费点 {a['used_by']}")
        else:
            notes.append(f"note: preview-only anchor '{token}' ({a['id']}) 不在本次探测的安装里；"
                         f"{a['impact']}")
    return violations, notes, oks


def _self_test() -> int:
    from gate_common import SelfTest

    st = SelfTest()
    anchors = [
        {"id": "a", "token": "TOKEN_A", "tier": "required", "used_by": "x", "impact": "y"},
        {"id": "b", "token": "TOKEN_B", "tier": "required", "used_by": "x", "impact": "y"},
        {"id": "c", "token": "TOKEN_C", "tier": "preview-only", "used_by": "x", "impact": "y"},
    ]
    with tempfile.TemporaryDirectory() as td:
        root = Path(td) / "dsh"
        lib = root / "node_modules" / "@deepseek-ai" / "dsh-client-ui-layout" / "lib"
        lib.mkdir(parents=True)
        (root / "package.json").write_text(
            json.dumps({"name": DSH_PACKAGE_NAME, "version": "0.0.0-fixture"}), encoding="utf-8")
        asset = lib / "client.js"
        asset.write_text("var s='TOKEN_A'; var t='TOKEN_B';\n", encoding="utf-8")
        (lib / "blob.png").write_bytes(b"TOKEN_A")

        st.ok(_package_root(root) == root, "dsh package root resolves by package name")
        st.ok(_package_root(Path(td)) is None, "non-package dir is not a dsh root")

        files = client_asset_files(root)
        st.ok([f.name for f in files] == ["client.js"],
              f"client assets select text bundles only: {[f.name for f in files]}")
        st.ok(client_asset_files(Path(td)) == [], "a root without client packages yields no assets")

        hits = probe(files, anchors)
        st.ok(hits["TOKEN_A"] == str(asset), "required token found and located")
        st.ok(hits["TOKEN_C"] is None, "absent token reported as None")

        v, n, oks = classify(anchors, hits)
        st.ok(v == [] and len(oks) == 2 and len(n) == 1,
              f"present required + missing preview-only: violations={v} notes={n}")
        st.ok("preview-only anchor 'TOKEN_C'" in n[0], "missing preview anchor is a note")

        # 词界：更长的近名不得顶替锚（评审实测：sidebarCollapsed 之于 sidebarCol）。
        asset.write_text("var s='TOKEN_A_LONGER';\n", encoding="utf-8")
        st.ok(probe(files, anchors)["TOKEN_A"] is None,
              "a longer identifier starting with the token does not satisfy it")

        # CSS 变量按定义形匹配：只剩 var(--x) 引用时不得算命中。
        var_anchor = [{"id": "v", "token": "--x-token", "tier": "required",
                       "used_by": "u", "impact": "i"}]
        asset.write_text("color:var(--x-token);\n", encoding="utf-8")
        st.ok(probe(files, var_anchor)["--x-token"] is None,
              "a bare var() reference is not a css-var definition")
        asset.write_text(":root{--x-token:#fff;--x-token-hover:#000}\n", encoding="utf-8")
        st.ok(probe(files, var_anchor)["--x-token"] == str(asset),
              "definition form found; a -suffixed sibling does not shadow it")

        # 缺 required = 本闸存在的理由。
        asset.write_text("var s='TOKEN_A';\n", encoding="utf-8")
        v2, _n2, _ = classify(anchors, probe(client_asset_files(root), anchors))
        st.ok(len(v2) == 1 and v2[0].startswith("missing required anchor 'TOKEN_B'"),
              f"missing required anchor is a violation: {v2}")

        # 清单形状：未知 tier / 重复 token / 缺字段 / 全 preview-only 皆违规。
        man = Path(td) / "m.json"
        man.write_text(json.dumps({"anchors": [
            {"id": "a", "token": "T", "tier": "nope", "used_by": "x", "impact": "y"},
            {"id": "b", "token": "T", "tier": "required", "used_by": "x", "impact": "y"},
            {"id": "c", "token": "U", "tier": "required", "used_by": "x"},
        ]}), encoding="utf-8")
        _anchors, errs = load_anchors(man)
        st.ok(any("tier" in e for e in errs) and any("duplicate token" in e for e in errs)
              and any("'impact'" in e for e in errs),
              f"manifest shape violations reported: {errs}")
        man.write_text(json.dumps({"anchors": [
            {"id": "a", "token": "T", "tier": "preview-only", "used_by": "x", "impact": "y"},
        ]}), encoding="utf-8")
        st.ok(any("no 'required' anchor" in e for e in load_anchors(man)[1]),
              "an all-preview-only register is a violation (it could never fail)")
        st.ok(load_anchors(Path(td) / "absent.json")[1] != [], "missing manifest is a violation")

    return st.finish("verify-upstream-anchors")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", default=DEFAULT_MANIFEST)
    ap.add_argument("--dsh-root", default=None,
                    help="dsh 包根（显式给出即须有效，不回退自动解析）")
    ap.add_argument("--enforce", action="store_true", help="缺 required 锚即 exit 1")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()

    if args.self_test:
        return _self_test()

    anchors, errors = load_anchors(Path(args.manifest))
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1

    # 显式指定即权威：指错了必须报错，绝不静默改探另一份安装（探 X 报 Y 的结论）。
    if args.dsh_root:
        root = _package_root(Path(args.dsh_root))
        if root is None:
            print(f"FAIL: --dsh-root {args.dsh_root} 不是 {DSH_PACKAGE_NAME} 包根"
                  "（包名不符或 package.json 不可读）")
            return 1
    else:
        root = resolve_auto_root()
        if root is None:
            print("SKIP: 未找到已装的 @deepseek-ai/dsh"
                  "（未显式给 --dsh-root；自动解析 npm root -g / PATH 两路皆无）")
            return 0

    files = client_asset_files(root)
    if not files:
        # 「找不到资产」与「锚被改名」必须分开：否则 required 全报 missing，诊断指向错处。
        print(f"FAIL: {root} 下未找到客户端资产（dsh-client-*/dsh-web-frontend 布局变更或资产被裁）"
              "——无法判定锚，不得视为通过")
        return 1

    hits = probe(files, anchors)
    violations, notes, oks = classify(anchors, hits)
    required = sum(1 for a in anchors if a["tier"] == "required")
    print(f"dsh {dsh_version(root)} ({root})；扫描 {len(files)} 个客户端资产文件")
    for line in oks:
        print(line)
    for line in notes:
        print(line)
    for v in violations:
        print(f"FAIL: {v}")
    if violations and args.enforce:
        print(f"upstream-anchors: {len(violations)}/{required} required 锚缺失")
        return 1
    if violations:
        print(f"upstream-anchors: {len(violations)}/{required} required 锚缺失（report 档：未判门）")
        return 0
    print(f"upstream-anchors: OK（{required} required 锚齐）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
