#!/usr/bin/env bash
# build-companion-tgz.sh — 从仓库源码现打 dsh-desktop-companion.tgz（安装器自带插件资源）。
# bundle-runtime-ci.sh 退役后（ADR online-first-unbundled-runtime 批次二）本脚本是插件 tgz 的
# 唯一生产点：package-*.sh 在 staging 时调用，产物放内容根 resources/plugins/ 下，
# 运行时由 MarketInstallHelper.ResolveCompanionSpec 的安装器资源通道解析。
# staging 目录法打出 package/ 前缀——macOS bsdtar 无 GNU tar 的 --transform，staging 三平台一致。
#
# 用法: build-companion-tgz.sh <out.tgz>
set -euo pipefail

OUT="${1:?usage: build-companion-tgz.sh <out.tgz>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/plugins/dsh-desktop-companion"

[[ -f "$SRC/package.json" ]] || { echo "error: 未找到 $SRC/package.json（桌面伴生插件源码缺失）" >&2; exit 1; }
[[ -f "$SRC/cordis.patch.yml" ]] || { echo "error: 未找到 $SRC/cordis.patch.yml（插件 patch 清单缺失）" >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/package"
cp "$SRC/package.json" "$SRC/cordis.patch.yml" "$TMP/package/"
cp -r "$SRC/lib" "$SRC/client" "$TMP/package/"

mkdir -p "$(dirname "$OUT")"
rm -f "$OUT"
(cd "$TMP" && tar -czf "$OUT" package)

# 校验 fail loud：name 正确（容忍上游缩进格式变化）+ 体积下限（防假包/空包）
pkg_name="$(tar -xOzf "$OUT" package/package.json 2>/dev/null \
  | grep -oE '"name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 \
  | sed 's/.*:[[:space:]]*"//; s/"$//' || true)"
if [[ "$pkg_name" != "dsh-desktop-companion" ]]; then
  echo "error: tgz 内 package.json name='$pkg_name'，期望 'dsh-desktop-companion'" >&2
  exit 1
fi
SZ=$(stat -c%s "$OUT" 2>/dev/null || stat -f%z "$OUT" 2>/dev/null || echo 0)
if [[ "$SZ" -lt 4096 ]]; then
  echo "error: tgz 过小（${SZ}B < 4096B），疑似假包/半截包" >&2
  exit 1
fi
echo "companion tgz: $OUT ($(du -h "$OUT" | cut -f1))"
