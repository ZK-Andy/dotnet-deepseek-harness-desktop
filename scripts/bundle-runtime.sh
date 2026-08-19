#!/usr/bin/env bash
# bundle-runtime.sh — 把本机 dsh 运行时闭包拷进 resources/runtime（POC；正式打包改为按平台下载/签名）。
# 产物：resources/runtime/{node, dsh/} —— 全部 gitignore，由发布流程组装。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NODE_SRC="${NODE_SRC:-$HOME/.hermes/node/bin/node}"
DSH_SRC="${DSH_SRC:-$HOME/.local/lib/node_modules/@deepseek-ai/dsh}"
DEST="$ROOT/resources/runtime"

[[ -x "$NODE_SRC" ]] || { echo "error: node 源不存在/不可执行: $NODE_SRC（可用 NODE_SRC= 覆盖）" >&2; exit 1; }
[[ -f "$DSH_SRC/lib/bin.js" ]] || { echo "error: dsh 源不存在: $DSH_SRC（可用 DSH_SRC= 覆盖）" >&2; exit 1; }

mkdir -p "$DEST/dsh"
echo "== 复制 node → $DEST/node"
cp "$NODE_SRC" "$DEST/node"
chmod +x "$DEST/node"
echo "== 复制 dsh 闭包 → $DEST/dsh/（约 300MB，请稍候）"
cp -r "$DSH_SRC/." "$DEST/dsh/"

echo "== 完成。结构："
ls -la "$DEST" | head -5
echo "  dsh/lib/bin.js: $([[ -f "$DEST/dsh/lib/bin.js" ]] && echo OK || echo MISSING)"
echo "若需覆盖运行时路径：export DSH_DESKTOP_RUNTIME_DIR='$DEST'"
