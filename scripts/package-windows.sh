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
  if [[ "$OSTYPE" == msys* || "$OSTYPE" == cygwin* || -n "${WINDIR:-}" ]]; then
    # Windows: 解引用拷贝，避免 junction/symlink 失败
    cp -Lr "$ROOT/resources/runtime" "$STAGE/resources/" 2>/dev/null || powershell -Command "Copy-Item -Path '$ROOT/resources/runtime' -Destination '$STAGE/resources' -Recurse -Force" 2>/dev/null || cp -r "$ROOT/resources/runtime" "$STAGE/resources/"
  else
    cp -a "$ROOT/resources/runtime" "$STAGE/resources/"
  fi
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

mkdir -p "$OUT"
ZIP="$OUT/DeepSeek.Harness.Desktop_${VERSION}_${ARCH}.zip"
rm -f "$ZIP"

# 兜底压缩：zip → 7z → tar (bsdtar -a) → powershell (cygpath 转换)
create_zip() {
  local src="$1" dst="$2"
  local src_dir src_base
  src_dir="$(dirname "$src")"
  src_base="$(basename "$src")"
  if command -v zip >/dev/null 2>&1; then
    echo "   尝试 zip"
    (cd "$src_dir" && zip -r -q "$dst" "$src_base") && return 0
    echo "   zip 失败，回退"
  fi
  if command -v 7z >/dev/null 2>&1; then
    echo "   尝试 7z"
    (cd "$src_dir" && 7z a -tzip "$dst" "$src_base" -mx=0 >/dev/null 2>&1) && return 0
    7z a -tzip "$dst" "$src" -mx=0 >/dev/null 2>&1 && return 0
    echo "   7z 失败，回退"
  fi
  if command -v tar >/dev/null 2>&1; then
    echo "   尝试 tar -a (bsdtar)"
    (cd "$src_dir" && tar -a -c -f "$dst" "$src_base" 2>/dev/null) && return 0
    (cd "$src_dir" && tar -c -f "$dst" "$src_base" 2>/dev/null) && return 0
    echo "   tar 失败，回退"
  fi
  local ps=""
  if command -v pwsh >/dev/null 2>&1; then ps="pwsh"
  elif command -v powershell >/dev/null 2>&1; then ps="powershell"
  fi
  if [[ -n "$ps" ]]; then
    echo "   尝试 $ps Compress-Archive"
    local src_win dst_win
    if command -v cygpath >/dev/null 2>&1; then
      src_win="$(cygpath -w "$src" 2>/dev/null || echo "$src")"
      dst_win="$(cygpath -w "$dst" 2>/dev/null || echo "$dst")"
    else
      # 回退：/d/a -> D:\a (仅处理常见盘符)
      src_win="$(echo "$src" | sed -E 's#^/([cCdD])/#\1:/#' | tr '/' '\\')"
      # 上行在 BSD sed 可能失败，二次回退
      if [[ "$src_win" == "$src" ]]; then
        src_win="$(echo "$src" | sed 's#^/d/#D:\\#; s#^/c/#C:\\#; s#/#\\#g')"
        dst_win="$(echo "$dst" | sed 's#^/d/#D:\\#; s#^/c/#C:\\#; s#/#\\#g')"
      else
        dst_win="$(echo "$dst" | sed -E 's#^/([cCdD])/#\1:/#' | tr '/' '\\')"
      fi
      # 修正首字符盘符大写
      src_win="$(echo "$src_win" | sed 's#^c:#C:#; s#^d:#D:#')"
      dst_win="$(echo "$dst_win" | sed 's#^c:#C:#; s#^d:#D:#')"
    fi
    echo "   ps src=$src_win dst=$dst_win"
    "$ps" -NoProfile -Command "Compress-Archive -Path '$src_win' -DestinationPath '$dst_win' -Force" 2>&1 | head -20
    if [[ -f "$dst" ]]; then return 0; fi
    "$ps" -NoProfile -Command "Compress-Archive -Path '$src_win\\*' -DestinationPath '$dst_win' -Force" 2>&1 | head -20
    if [[ -f "$dst" ]]; then return 0; fi
  fi
  return 1
}

if ! create_zip "$STAGE" "$ZIP"; then
  echo "error: 无法创建 zip（zip/7z/tar/powershell 均失败）" >&2; exit 1
fi
echo "== 产物: $ZIP ($(du -h "$ZIP" 2>/dev/null | cut -f1 || ls -lh "$ZIP" | awk '{print $5}'))"
if command -v unzip >/dev/null 2>&1; then
  unzip -l "$ZIP" 2>&1 | head -20
elif command -v 7z >/dev/null 2>&1; then
  7z l "$ZIP" 2>&1 | head -40
elif command -v tar >/dev/null 2>&1; then
  tar -tf "$ZIP" 2>&1 | head -20
else
  local ps2="powershell"; command -v pwsh >/dev/null 2>&1 && ps2="pwsh"
  if command -v cygpath >/dev/null 2>&1; then
    ZIP_WIN="$(cygpath -w "$ZIP" 2>/dev/null || echo "$ZIP")"
  else
    ZIP_WIN="$ZIP"
  fi
  "$ps2" -NoProfile -Command "Get-ChildItem '$ZIP_WIN' | Format-List; try { (Get-ChildItem '$ZIP_WIN').Length } catch {}" 2>/dev/null | head -20 || ls -lh "$ZIP" | head -5
fi
