#!/usr/bin/env bash
# bundle-runtime-ci.sh — 在 CI（或任意干净机）生成 resources/runtime，免本机 dsh 安装拷贝：
#   1) 从 nodejs.org 下载平台 Node 二进制 → resources/runtime/node
#   2) npm 安装固定版本 @deepseek-ai/dsh 依赖闭包 → resources/runtime/dsh
# 与 scripts/bundle-runtime.sh（本机拷贝版）产物布局一致：resources/runtime/{node, dsh/}
set -euo pipefail

PLATFORM="${1:-linux-x64}"
NODE_VERSION="${NODE_VERSION:-22.23.1}"
DSH_VERSION="${DSH_VERSION:-0.1.0-rc.7}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/resources/runtime"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

case "$PLATFORM" in
  linux-x64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.xz"; NODE_BIN="node-v${NODE_VERSION}-linux-x64/bin/node" ;;
  linux-arm64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-arm64.tar.xz"; NODE_BIN="node-v${NODE_VERSION}-linux-arm64/bin/node" ;;
  *) echo "error: 暂不支持平台 $PLATFORM" >&2; exit 1 ;;
esac

echo "== [1/3] 下载 Node v${NODE_VERSION} ($PLATFORM)"
curl -fsSL "$NODE_URL" -o "$TMP/node.tar.xz"
tar -xJf "$TMP/node.tar.xz" -C "$TMP"
mkdir -p "$DEST"
cp "$TMP/$NODE_BIN" "$DEST/node"
chmod +x "$DEST/node"

echo "== [2/3] npm 安装 @deepseek-ai/dsh@${DSH_VERSION} 依赖闭包"
mkdir -p "$TMP/app"
cd "$TMP/app"
npm init -y >/dev/null 2>&1
npm install "@deepseek-ai/dsh@${DSH_VERSION}" --no-save --omit=dev >/dev/null 2>&1

echo "== [3/3] 组装 resources/runtime/dsh"
rm -rf "$DEST/dsh"
mkdir -p "$DEST/dsh"
cp -r node_modules/@deepseek-ai/dsh/. "$DEST/dsh/"

echo "== 完成 → $DEST"
"$DEST/node" -v
echo "dsh 版本: $(grep '"version"' "$DEST/dsh/package.json" | head -1)"
