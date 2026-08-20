#!/usr/bin/env bash
# package-windows.sh — 从 .NET publish 输出打 Windows 包（zip）。
# 布局：publish 全量 + resources/runtime
# 用法：
#   scripts/package-windows.sh [publish_dir]
#   scripts/package-windows.sh --stage-only [dir]
# 环境：VERSION、ARCH（x64/arm64，现仅 x64 有完整测试）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARG1="${1:-}"
STAGE_ONLY=0
if [[ "$ARG1" == "--stage-only" ]]; then STAGE_ONLY=1; ARG1="${2:-}"; fi

ARCH_RAW="${ARCH:-x64}"
case "$ARCH_RAW" in
  x64|amd64|x86_64) ARCH="x64"; RID="win-x64"; OUT_SUFFIX="win-x64" ;;
  arm64|aarch64) ARCH="arm64"; RID="win-arm64"; OUT_SUFFIX="win-arm64" ;;
  *) echo "error: 不支持 ARCH=$ARCH_RAW" >&2; exit 1 ;;
esac

PUBLISH_DIR="${ARG1:-$ROOT/artifacts/publish-$RID}"
VERSION="${VERSION:-0.1.0}"
OUT="$ROOT/artifacts/$OUT_SUFFIX"
STAGE="$OUT/stage/DeepSeek.Harness.Desktop"

[[ -d "$PUBLISH_DIR" ]] || { echo "error: publish 目录不存在: $PUBLISH_DIR" >&2; exit 1; }

echo "== 组装 staging: $STAGE"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp -r "$PUBLISH_DIR/." "$STAGE/"

if [[ -d "$ROOT/resources/runtime" ]]; then
  echo "   并入 resources/runtime"
  mkdir -p "$STAGE/resources"
  cp -a "$ROOT/resources/runtime" "$STAGE/resources/"
  if [[ ! -f "$STAGE/resources/runtime/node.exe" && ! -f "$STAGE/resources/runtime/node" ]]; then
    echo "warn: staging 缺 node(.exe)，可能架构不匹配 ($RID)" >&2
  fi
  if [[ ! -f "$STAGE/resources/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js" ]]; then
    echo "warn: stagng 缺 dsh 入口" >&2
  fi
  if [[ -f "$STAGE/resources/runtime/dshmarket.tgz" ]]; then
    SZ=$(stat -c%s "$STAGE/resources/runtime/dshmarket.tgz" 2>/dev/null || stat -f%z "$STAGE/resources/runtime/dshmarket.tgz" 2>/dev/null || echo 0)
    if [[ "$SZ" -lt 10240 ]]; then echo "error: dshmarket.tgz 过小" >&2; exit 1; fi
  fi
fi

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  find "$STAGE" -maxdepth 2 -type d | sort | head -20
  ls -lh "$STAGE/resources/runtime/node"* 2>&1 | head -5 || echo "node 缺失"
  exit 0
fi

command -v zip >/dev/null || { echo "error: 缺 zip" >&2; exit 1; }
mkdir -p "$OUT"
ZIP="$OUT/DeepSeek.Harness.Desktop_${VERSION}_${ARCH}.zip"
rm -f "$ZIP"
(cd "$(dirname "$STAGE")" && zip -r -q "$ZIP" "$(basename "$STAGE")")
echo "== 产物: $ZIP ($(du -h "$ZIP" | cut -f1))"
unzip -l "$ZIP" | head -20
