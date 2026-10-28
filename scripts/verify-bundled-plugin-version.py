#!/usr/bin/env python3
"""随包插件「出货面改了就必须 bump 版本」闸（机制与理由见 ADR bundled-plugin-version-bump-gate）。

版本号是随包插件唯一的换入触发器（`UpdateVersion.Compare(bundled, installed) > 0`）：内容改了而
版本不动，已装用户就永久停在旧副本且零报错（2026-10-28 八语言化事故，见 ADR）。

判据（对应 ADR Decision）：A 清单取自 `BundledPluginSupply.All`、出货面取自 tgz 脚本的 cp 源；
B 触及出货面则版本须严格变大（`test/`、`testkit/`、README 不进 tgz）；C 出货面必须覆盖每个 cp 源、
且每个成员有改动都须落在覆盖内，否则判红；D base 侧无 `package.json` = 新插件（豁免），读失败或
新侧读不到判红；E 变更集不可判定判红。与运行时对齐两处：diff 带 `--no-renames`（重命名折叠会让
「出货面文件被移走」隐身）；版本串按 `UpdateVersion.ParseSegments` 逐段校验，不可解析即判红。

Usage: [--repo ROOT] [--staged|--since BASE] [--enforce] | --self-test；exit 0 = pass, 1 = violations.
"""

from __future__ import annotations

import argparse
import json
import re
import shlex
import subprocess
import sys
from pathlib import Path

from gate_common import SelfTest

SUPPLY_REL = "src/DeepSeek.Harness.Desktop.Infrastructure/Plugins/BundledPluginSupply.cs"
TGZ_SCRIPT_REL = "scripts/build-companion-tgz.sh"
PLUGIN_ROOT_REL = "plugins"

ENTRY_RE = re.compile(r'new\(\s*"(?P<pkg>[^"]+)"\s*,')


def _git(repo: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=repo, capture_output=True, text=True,
                          errors="replace", check=False)
    if proc.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {proc.stderr.strip()}")
    return proc.stdout


def _diff_paths(repo: Path, args: list[str]) -> set[str]:
    """Paths one diff moment reports.

    `--no-renames` keeps a moved shipped file visible on both sides — with rename
    detection on, `lib/x.js → test/x.js` reports only the target and the shipped-side
    removal becomes invisible.
    """
    return {ln.strip() for ln in _git(repo, *args, "--name-only", "--no-renames").splitlines()
            if ln.strip()}


def _changed_paths(repo: Path, mode: str, since: str) -> list[str] | None:
    """Changed paths for the git moment; None = undeterminable (caller fails loud)."""
    try:
        if mode == "staged":
            out = _diff_paths(repo, ["diff", "--cached"])
        elif mode == "since":
            out = _diff_paths(repo, ["diff", f"{since}..HEAD"])
        else:
            out = _diff_paths(repo, ["diff", "HEAD"])
            out |= {ln.strip() for ln in _git(
                repo, "ls-files", "--others", "--exclude-standard").splitlines() if ln.strip()}
    except RuntimeError:
        return None
    return sorted(out)


def _read_at(repo: Path, rev: str, rel: str) -> tuple[str, str | None]:
    """Content of `rel` at `rev` (`rev` = "" means the index, i.e. `:path`).

    Three distinct states: ("absent", None) = no such path (a plugin base never had);
    ("error", None) = git could not answer, which must not read as "new plugin";
    ("ok", text) otherwise.
    """
    ref = f"{rev}:{rel}"
    try:
        _git(repo, "cat-file", "-e", ref)
    except RuntimeError:
        return "absent", None
    try:
        return "ok", _git(repo, "show", ref)
    except RuntimeError:
        return "error", None


def version_of(package_json: str) -> str | None:
    """顶层 `version` 字段；非 JSON 或缺字段返回 None（由调用方判红）。"""
    try:
        value = json.loads(package_json).get("version")
    except (json.JSONDecodeError, AttributeError):
        return None
    return value if isinstance(value, str) and value.strip() else None


class BadVersion(ValueError):
    """版本串不在 `UpdateVersion.ParseSegments` 的接受面内。"""


def parse_segments(version: str) -> list[int]:
    """`UpdateVersion.ParseSegments` 的忠实移植：剥 `v`/`V` 前缀、`-` 处截断、逐段整数。

    任一段非整数即抛（C# 侧同样抛，`BundledPluginCatalog` 捕获后跳过升级检查）。
    """
    core = version.strip().lstrip("vV")
    dash = core.find("-")
    if dash >= 0:
        core = core[:dash]
    segs: list[int] = []
    for part in core.split("."):
        if not part.isdigit():
            raise BadVersion(version)
        segs.append(int(part))
    return segs


def is_newer(candidate: str, base: str) -> bool:
    """`UpdateVersion.Compare(candidate, base) > 0`；不可解析的串抛 `BadVersion`。"""
    left, right = parse_segments(candidate), parse_segments(base)
    width = max(len(left), len(right))
    left += [0] * (width - len(left))
    right += [0] * (width - len(right))
    return left > right


def shipped_paths(repo: Path) -> tuple[set[str], str | None]:
    """出货面 = tgz 生产脚本的 `cp` 源（现解析，不另立常量）。"""
    script = repo / TGZ_SCRIPT_REL
    try:
        text = script.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return set(), f"{TGZ_SCRIPT_REL} 不存在——出货面无法判定（fail loud）"
    text = re.sub(r"\\\n", " ", text)  # 续行先归并，否则行首 `cp` 判据会漏掉续行里的源
    found: set[str] = set()
    for raw in text.splitlines():
        line = raw.strip()
        if not line.startswith("cp "):
            continue
        try:
            # comments=True：行尾注释会被剥掉，否则 `#` 与其后的词会让末位不再是目标（误红）。
            tokens = shlex.split(line, comments=True)
        except ValueError:
            return found, f"{TGZ_SCRIPT_REL} 的 cp 行无法解析：{line}"
        positional = [t for t in tokens[1:] if not t.startswith("-")]
        # 目标经 `--target-directory=` 表达时全部位置参数都是源，否则末位是目标。硬猜「末位必是目标」
        # 会把该形态下的末位真源从守卫面静默丢掉（判据 C 禁止）。
        sources = positional if any(t.startswith("--target-directory") for t in tokens) else positional[:-1]
        for tok in sources:
            name = tok[len("$SRC/"):].rstrip("/") if tok.startswith("$SRC/") else ""
            if not name:
                return found, f"{TGZ_SCRIPT_REL} 的 cp 源 `{tok}` 不是 `$SRC/<x>`（出货面无法判定）"
            found.add(name)
    if not found:
        return set(), f"{TGZ_SCRIPT_REL} 内未解析到 `cp ... \"$SRC/<x>\"` 行——判据失效（fail loud）"
    return found, None


def bundled_packages(repo: Path) -> tuple[list[str], str | None]:
    """随包插件清单（装配侧唯一事实源）。"""
    try:
        text = (repo / SUPPLY_REL).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return [], f"{SUPPLY_REL} 不存在——随包清单无法判定（fail loud）"
    pkgs = [m.group("pkg") for m in ENTRY_RE.finditer(text)]
    if not pkgs:
        return [], f"{SUPPLY_REL} 内未解析到清单项——闸失去判定面（fail loud）"
    return pkgs, None


def scan(repo: Path, mode: str = "worktree", since: str = "") -> list[str]:
    pkgs, err = bundled_packages(repo)
    if err:
        return [err]
    shipped, ship_err = shipped_paths(repo)
    violations: list[str] = [ship_err] if ship_err else []

    changed = _changed_paths(repo, mode, since)
    if changed is None:
        return violations + ["变更集无法判定（base 不可达或 git 失败）——不得放行"]

    for pkg in pkgs:
        prefix = f"{PLUGIN_ROOT_REL}/{pkg}/"
        own = [p[len(prefix):] for p in changed if p.startswith(prefix)]
        touched = [f"{prefix}{o}" for o in own if o.split("/")[0] in shipped]
        if not touched:
            # test/、testkit/、README 不进 tgz，豁免；其余改动落在出货面外即「出货面不可判定」——
            # 静默 continue 会让新增的清单成员完全脱离判据。
            if {o.split("/")[0] for o in own} - {"test", "testkit", "README.md"}:
                violations.append(f"{prefix}: 有改动但 {TGZ_SCRIPT_REL} 的出货面未覆盖它"
                                  f"（出货面不可判定）")
            continue

        rel = f"{prefix}package.json"
        if mode == "worktree":
            try:
                state, new_side = "ok", (repo / rel).read_text(encoding="utf-8", errors="replace")
            except OSError:
                state, new_side = "absent", None
        else:
            state, new_side = _read_at(repo, "" if mode == "staged" else "HEAD", rel)
        if state == "absent" or new_side is None:
            violations.append(f"{rel}: 出货面有改动但读不到新版本（fail loud）")
            continue
        new_version = version_of(new_side)
        if new_version is None:
            violations.append(f"{rel}: 缺 version 字段或不是合法 JSON")
            continue

        base_rev = since if mode == "since" else "HEAD"
        base_state, old_side = _read_at(repo, base_rev, rel)
        if base_state == "error":
            violations.append(f"{rel}: base 侧 `{base_rev}` 读失败（无法判定是否 bump）")
            continue
        if base_state == "absent":
            continue  # 插件在 base 尚不存在：无旧版本可比，递增规则不适用
        old_version = version_of(old_side or "")
        if old_version is None:
            violations.append(f"{rel}: base 侧缺 version 字段（无法判定是否 bump）")
            continue

        try:
            newer = is_newer(new_version, old_version)
        except BadVersion:
            violations.append(
                f"{rel}: 版本串不可解析（{old_version} → {new_version}）——运行时 "
                f"`UpdateVersion.Compare` 会抛错并被 `BundledPluginCatalog` 捕获跳过升级检查，"
                f"已装用户拿不到新内容")
            continue
        if not newer:
            violations.append(
                f"{rel}: 出货面已改（{', '.join(sorted(touched))}）但版本未升 "
                f"{old_version} → {new_version}——已装用户会被「同版或更高跳过」永久留在旧内容；"
                f"改包内容必须 bump 版本")
    return violations


def _self_test() -> int:
    st = SelfTest()
    import tempfile

    def git(repo: Path, *argv: str) -> None:
        subprocess.run(["git", "-C", str(repo), *argv], check=True, capture_output=True, text=True)

    with tempfile.TemporaryDirectory() as td:
        repo = Path(td) / "e2e"
        repo.mkdir()
        git(repo, "init", "-q")
        git(repo, "config", "user.email", "t@t")
        git(repo, "config", "user.name", "t")
        (repo / SUPPLY_REL).parent.mkdir(parents=True, exist_ok=True)
        (repo / SUPPLY_REL).write_text(
            'new("dsh-desktop-companion", MarketInstallHelper.ResolveCompanionSpec),\n',
            encoding="utf-8")
        (repo / TGZ_SCRIPT_REL).parent.mkdir(parents=True, exist_ok=True)
        (repo / TGZ_SCRIPT_REL).write_text(
            'cp "$SRC/package.json" "$SRC/cordis.patch.yml" "$TMP/package/"\n'
            'cp -r "$SRC/lib" "$SRC/client" "$TMP/package/"\n', encoding="utf-8")
        pkg = repo / PLUGIN_ROOT_REL / "dsh-desktop-companion"
        for sub, text in (("client", "var a = 1\n"), ("lib", "export {}\n"), ("test", "test('x')\n")):
            (pkg / sub).mkdir(parents=True)
            (pkg / sub / f"{sub}.js").write_text(text, encoding="utf-8")
        (pkg / "package.json").write_text('{\n  "version": "0.0.23"\n}\n', encoding="utf-8")
        (pkg / "cordis.patch.yml").write_text("- id: x\n", encoding="utf-8")
        git(repo, "add", "-A")
        git(repo, "commit", "-qm", "base")

        def bump(version: str) -> None:
            (pkg / "package.json").write_text(
                '{\n  "version": "' + version + '"\n}\n', encoding="utf-8")

        # 1) 出货面改了、版本不动 —— 本闸要拦的形态（2026-10-28 八语言化事故）
        (pkg / "client" / "client.js").write_text("var a = 2\n", encoding="utf-8")
        git(repo, "add", "-A")
        st.ok(any("版本未升" in r for r in scan(repo, "staged")), "出货面改动 + 版本不动判红")

        # 2) 同一次改动带上版本递增 —— 放行
        bump("0.0.24")
        git(repo, "add", "-A")
        st.ok(scan(repo, "staged") == [], "出货面改动 + 版本递增放行")

        # 3) 降版同样判红（换入只看「更新」）
        bump("0.0.22")
        git(repo, "add", "-A")
        st.ok(any("版本未升" in r for r in scan(repo, "staged")), "降版判红")

        # 4) 测试面改动不要求 bump（test/ 不进 tgz）
        bump("0.0.24")
        git(repo, "add", "-A")
        git(repo, "commit", "-qm", "bump")
        (pkg / "test" / "test.js").write_text("test('y')\n", encoding="utf-8")
        git(repo, "add", "-A")
        st.ok(scan(repo, "staged") == [], "仅 test/ 改动不要求 bump")

        # 5) cordis.patch.yml 也算出货面
        (pkg / "cordis.patch.yml").write_text("- id: y\n", encoding="utf-8")
        git(repo, "add", "-A")
        st.ok(any("cordis.patch.yml" in r for r in scan(repo, "staged")),
              "cordis.patch.yml 改动要求 bump")
        git(repo, "commit", "-qm", "bump2")

        # 6) 出货面文件被改名移出（git 认作重命名）：--no-renames 让它两侧可见，
        #    否则 lib/index.js → test/index.js 完全隐身，版本不动也判绿。
        git(repo, "mv", "plugins/dsh-desktop-companion/lib/lib.js",
            "plugins/dsh-desktop-companion/test/lib.js")
        st.ok(any("版本未升" in r for r in scan(repo, "staged")),
              "出货面文件重命名移出（rename 折叠）仍判红")

        # 7) --since 真实路径：CI 的唯一执行档，必须真的走到 _read_at
        git(repo, "commit", "-qm", "move")
        (pkg / "client" / "client.js").write_text("var a = 3\n", encoding="utf-8")
        git(repo, "add", "-A")
        git(repo, "commit", "-qm", "no-bump")
        st.ok(any("版本未升" in r for r in scan(repo, "since", "HEAD~1")),
              "--since 档判红（改动未 bump）")
        bump("0.0.25")
        git(repo, "add", "-A")
        git(repo, "commit", "-qm", "bumped")
        st.ok(scan(repo, "since", "HEAD~2") == [], "--since 档放行（改动已 bump）")

        # 8) cp -a / 续行写法必须解析得到，不得报假「漂移」
        (repo / TGZ_SCRIPT_REL).write_text(
            'cp -a "$SRC/package.json" \\\n'
            '      "$SRC/cordis.patch.yml" "$TMP/package/"\n'
            'cp -r "$SRC/lib/" "$SRC/client" "$TMP"  # 行尾注释\n', encoding="utf-8")
        found, err = shipped_paths(repo)
        st.ok(err is None and found == {"package.json", "cordis.patch.yml", "lib", "client"},
              f"cp -a / 续行 / 尾斜杠 / 行尾注释解析完整：{sorted(found)} err={err}")

        # 9) 解析不到的 cp 源即判红（不许静默缩小守卫面）；目标经选项表达时真源不得被当目标丢掉
        for script, why in (
                ('cp "$OTHER/package.json" "$TMP/"\n', "非 $SRC/ 形态的 cp 源判红"),
                ("# no cp here\n", "一个 cp 源都解析不到即判红（判据失效不等于通过）"),
                ('cp --target-directory="$TMP/p" -r "$SRC/lib" "$SRC/client"\n',
                 "--target-directory 形态下末位真源仍算源（不靠末位猜目标）")):
            (repo / TGZ_SCRIPT_REL).write_text(script, encoding="utf-8")
            got, err = shipped_paths(repo)
            st.ok(err is not None if why.startswith(("非", "一个")) else err is None
                  and got == {"lib", "client"}, why)

        # 10) 不可判定变更集；插件有改动而出货面未覆盖它 —— 都判红而非放行
        st.ok(any("无法判定" in r for r in scan(repo, "since", "0" * 40)), "不可判定 diff 判红")
        (pkg / "src").mkdir(exist_ok=True)
        (pkg / "src" / "deep.js").write_text("x\n", encoding="utf-8")
        git(repo, "add", "-A")
        st.ok(any("出货面未覆盖" in r for r in scan(repo, "staged")), "出货面未覆盖的改动判红")

    # 11) 版本语义与 UpdateVersion.ParseSegments 对齐
    for lo, hi, expect, why in (
            ("0.0.23", "0.0.24", True, "0.0.24 > 0.0.23"),
            ("0.0.23", "0.0.23", False, "同版不判递增"),
            ("0.0.23", "0.0.9", False, "0.0.9 < 0.0.23（数字段而非字典序）"),
            ("0.9.9", "1.0", True, "段数不等按缺失段 0 比较"),
            ("0.0.23", "v0.0.24", True, "`v` 前缀与 C# 一致地剥掉"),
            ("0.0.24", "0.0.24-1", False, "`-` 后缀截断：0.0.24-1 == 0.0.24")):
        st.ok(is_newer(hi, lo) is expect, why)
    for bad in ("0.0.24+build", "1.0.a", ""):
        try:
            is_newer(bad, "0.0.23")
        except BadVersion:
            st.ok(True, f"不可解析版本抛 BadVersion：{bad!r}")
        else:
            st.ok(False, f"不可解析版本应抛 BadVersion：{bad!r}")

    # 12) 顶层 version，不被嵌套键骗
    st.ok(version_of('{"deps":{"x":{"version":"9.9.9"}},"version":"0.0.24"}') == "0.0.24",
          "version_of 取顶层键")
    st.ok(version_of("not json") is None, "非 JSON 返回 None")

    return st.finish("verify-bundled-plugin-version")


def main() -> int:
    parser = argparse.ArgumentParser(description="随包插件版本 bump 闸（ADR bundled-plugin-version-bump-gate）")
    parser.add_argument("--repo", default=".", help="repo root (default cwd)")
    parser.add_argument("--enforce", action="store_true", help="exit 1 when violations exist")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--staged", action="store_true", help="index vs HEAD (pre-commit)")
    group.add_argument("--since", default="", help="BASE..HEAD (pre-push/CI)")
    parser.add_argument("--self-test", action="store_true", help="run offline fixtures")
    args = parser.parse_args()
    if args.self_test:
        return _self_test()

    repo = Path(args.repo).resolve()
    mode = "staged" if args.staged else ("since" if args.since else "worktree")
    rows = scan(repo, mode, args.since)
    if rows:
        print(f"bundled-plugin-version: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        return 1 if args.enforce else 0
    print("bundled-plugin-version: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
