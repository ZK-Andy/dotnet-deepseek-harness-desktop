#!/usr/bin/env python3
"""Core 纯度闸：Core 源文件不得直接做文件/环境 IO（ADR core-purity-boundaries）。

「Core 是纯逻辑」此前只在**程序集层**成立（零外层项目引用、零外层程序集引用）。程序集边界挡不住
**进程环境面**：Core 自己读盘、读环境变量时，依赖方向照样是「Core → BCL」，编译器与既有架构测试都
看不见。本闸把 Core 的 IO 触点变成可执行判据：IO 经 Core 端口、适配器落 Infrastructure，Core 只留
纯函数与判定。

扫描面：`src/DeepSeek.Harness.Desktop.Core/**/*.cs`（排除 obj/bin）。
判据：出现文件/环境/进程 IO 触点即判红。注释与字符串内的同形词不算触点——扫描用
`gate_common.CSharpLineScanner`（剥注释、字符串内容留空；块注释跨行跟踪）。

禁用清单是**枚举式**的：新增 IO 形态（zip/tgz/注册表等）须同步进清单，否则门有缺口——故清单在
本文件头部单点维护，改它即改门禁（复核时优先看这一处）。

Usage:
    python3 scripts/verify-core-purity.py [--repo ROOT] [--enforce]
    python3 scripts/verify-core-purity.py --self-test
Exit code 0 = pass, 1 = violations.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

from gate_common import CSharpLineScanner, SelfTest, repo_root

CORE_REL = "src/DeepSeek.Harness.Desktop.Core"

# 禁用清单（单点维护）：Core 不得出现的 IO 触点。
FORBIDDEN = [
    r"\bFile\.",
    r"\bDirectory\.",
    r"\bFileStream\b",
    r"\bFileInfo\b",
    r"\bDirectoryInfo\b",
    r"\bStreamReader\b",
    r"\bStreamWriter\b",
    r"\bZipFile\b",
    r"\bZipArchive\b",
    r"\bTarReader\b",
    r"\bGZipStream\b",
    r"\bDeflateStream\b",
    r"\bBrotliStream\b",
    r"\bRegistryKey\b",
    r"\bRegistry\b",
    r"\bEnvironment\.GetEnvironmentVariable",
    r"\bEnvironment\.SetEnvironmentVariable",
    r"\bEnvironment\.GetFolderPath",
    r"\bEnvironment\.ProcessPath",
    r"\bEnvironment\.CurrentDirectory",
    r"\bEnvironment\.ExpandEnvironmentVariables",
    r"\bPath\.GetTempPath",
    r"\bPath\.GetTempFileName",
    r"\bProcess\.Start\b",
    r"\bnew Process\b",
]


def core_sources(repo: Path) -> list[Path]:
    core_dir = repo / CORE_REL
    if not core_dir.is_dir():
        return []
    out: list[Path] = []
    for path in sorted(core_dir.rglob("*.cs")):
        parts = path.parts
        if "obj" in parts or "bin" in parts:
            continue
        out.append(path)
    return out


def scan(repo: Path) -> list[str]:
    violations: list[str] = []
    sources = core_sources(repo)
    if not sources:
        return [f"Core 源目录不存在或为空: {CORE_REL}（判据失效，fail loud）"]
    for path in sources:
        text = path.read_text(encoding="utf-8", errors="replace")
        lines = text.splitlines()
        cleaned = list(CSharpLineScanner(lines).iter_cleaned())
        rel = path.relative_to(repo)
        for idx, code in enumerate(cleaned):
            if not code.strip():
                continue
            for token in FORBIDDEN:
                if re.search(token, code):
                    violations.append(f"{rel}:{idx + 1}: Core 不得直接做 IO 触点 {token}")
    return violations


def _self_test() -> int:
    st = SelfTest()
    import tempfile

    with tempfile.TemporaryDirectory() as td:
        repo = Path(td)
        core = repo / CORE_REL
        core.mkdir(parents=True, exist_ok=True)

        # 1) 合规：纯函数 + 注释里的同形词不算触点
        (core / "Ok.cs").write_text(
            "namespace X;\n"
            "// 旧实现曾用 File.ReadAllText（注释不算触点）\n"
            "public static class Ok\n{\n"
            "    public static string Name => \"File.ReadAllText\";\n"
            "}\n",
            encoding="utf-8")
        st.ok(scan(repo) == [], "合规样例通过（注释与字符串里的同形词不判红）")

        # 2) 直接读盘 → 判红
        (core / "Bad.cs").write_text(
            "namespace X;\n"
            "public static class Bad\n{\n"
            "    public static bool Exists(string p) => File.Exists(p);\n"
            "}\n",
            encoding="utf-8")
        rows = scan(repo)
        st.ok(any("File\\." in r for r in rows), "直接读盘判红")

        # 3) 读环境变量 → 判红
        (core / "Bad.cs").write_text(
            "namespace X;\n"
            "public static class Bad\n{\n"
            "    public static string? Lang() => Environment.GetEnvironmentVariable(\"LANG\");\n"
            "}\n",
            encoding="utf-8")
        rows = scan(repo)
        st.ok(any("GetEnvironmentVariable" in r for r in rows), "读环境变量判红")

        # 4) 块注释内的触点不算触点
        (core / "Bad.cs").write_text(
            "namespace X;\n"
            "public static class Bad\n{\n"
            "    /* 旧实现：\n"
            "       File.ReadAllText(path)\n"
            "    */\n"
            "    public static int Value => 1;\n"
            "}\n",
            encoding="utf-8")
        st.ok(scan(repo) == [], "块注释内的触点不判红")

    return st.finish("verify-core-purity")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=".", help="repo root (default cwd)")
    parser.add_argument("--enforce", action="store_true", help="exit 1 when violations exist")
    parser.add_argument("--self-test", action="store_true", help="run offline fixtures")
    args = parser.parse_args()
    if args.self_test:
        return _self_test()

    repo = Path(args.repo).resolve()
    rows = scan(repo)
    if rows:
        print(f"core-purity: {len(rows)} violation(s)")
        for r in rows:
            print(r)
        return 1 if args.enforce else 0
    print("core-purity: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
