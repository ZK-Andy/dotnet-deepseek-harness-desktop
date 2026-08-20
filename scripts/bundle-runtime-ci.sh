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

echo "== [2/3] pnpm 安装 @deepseek-ai/dsh@${DSH_VERSION} 依赖闭包"
if ! command -v pnpm >/dev/null 2>&1; then
  echo "    未发现 pnpm，npm install -g pnpm@11"
  npm install -g pnpm@11
fi
pnpm --version
mkdir -p "$TMP/app"
cd "$TMP/app"
npm init -y >/dev/null 2>&1
# 允许原生绑定（pty/FFI/proto）运行构建脚本，否则闭包缺 native 二进制且 pnpm11 直接报错
pnpm add "@deepseek-ai/dsh@${DSH_VERSION}" --prod \
  --allow-build=node-pty --allow-build=koffi --allow-build=protobufjs \
  --allow-build=@google/genai --allow-build=@deepseek-ai/dsh-subprocess-local

echo "== [3/3] 组装 resources/runtime（整棵 node_modules，pilot-harness 同款方案）"
rm -rf "$DEST/dsh" "$DEST/node_modules"
mkdir -p "$DEST/node_modules"
# 保留 pnpm 内部相对 symlink 结构整树拷入：入口 node_modules/@deepseek-ai/dsh/lib/bin.js
cp -a node_modules/. "$DEST/node_modules/"

echo "== [4/4] 自检：spawn dsh web 应给出 URL（最长 60s，失败打印 dsh 输出）"
SMOKE_HOME="$(mktemp -d)"
# dsh web 会常驻，timeout 到点必返回非 0（124/143）——只看日志里有无 URL
timeout 60 env DSH_HOME="$SMOKE_HOME" DEEPSEEK_API_KEY=placeholder \
     "$DEST/node" "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" --profile web --port 0 \
     >"$TMP/smoke.log" 2>&1 || true
if grep -q "dsh web:" "$TMP/smoke.log"; then
  echo "   自检 OK（dsh web 可启动）：$(grep 'dsh web:' "$TMP/smoke.log" | head -1)"
else
  echo "error: 闭包自检失败——dsh 未给出 URL。dsh 输出尾部："
  tail -20 "$TMP/smoke.log" >&2
  rm -rf "$SMOKE_HOME"
  exit 1
fi
rm -rf "$SMOKE_HOME"

echo "== 完成 → $DEST"
"$DEST/node" -v
echo "dsh 版本: $(grep '"version"' "$DEST/node_modules/@deepseek-ai/dsh/package.json" | head -1)"
du -sh "$DEST" | cut -f1
