#!/usr/bin/env bash
# bundle-runtime-ci.sh — 在 CI（或任意干净机）生成 resources/runtime（pilot-harness 同款整树方案）。
# 参照 pilot-harness：声明式依赖 @deepseek-ai/dsh 的完整闭包随产物收入，入口
# node_modules/@deepseek-ai/dsh/lib/bin.js；asar=false 思想下保留原样不打包为单文件。
# 产物：resources/runtime/{node, node_modules/...}（gitignore，发布时由 package-linux.sh 组装）
#   node: 来自 nodejs.org 的平台二进制
#   node_modules: pnpm 安装的完整依赖树（含跨平台 prebuild，原样保留 symlink 结构，入口同 pilot-harness apps/desktop/src/main.ts）
# 用法：bash scripts/bundle-runtime-ci.sh [linux-x64|linux-arm64]
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

echo "== [2/3] pnpm 安装 @deepseek-ai/dsh@${DSH_VERSION} + dshmarket 依赖闭包（dshmarket 随包预装，首启无下载）"
if ! command -v pnpm >/dev/null 2>&1; then
  echo "    未发现 pnpm，npm install -g pnpm@11"
  npm install -g pnpm@11
fi
pnpm --version
mkdir -p "$TMP/app"
cd "$TMP/app"
npm init -y >/dev/null 2>&1
# 允许原生绑定构建脚本，否则 pnpm 11 将拒绝执行且闭包缺 .node 二进制
pnpm add "@deepseek-ai/dsh@${DSH_VERSION}" --prod \
  --allow-build=node-pty --allow-build=koffi --allow-build=protobufjs \
  --allow-build=@google/genai --allow-build=@deepseek-ai/dsh-subprocess-local
# 预装市场：与 dsh 同闭包，随包收入，首启 patch 无需联网
pnpm add "dshmarket@1.15.0" --prod --allow-build=esbuild

echo "== [3/3] 组装 resources/runtime（整棵 node_modules，pilot-harness 同款）"
rm -rf "$DEST/dsh" "$DEST/node_modules"
mkdir -p "$DEST/node_modules"
# 保留 pnpm 内部相对 symlink 结构整树拷入；pilot-harness 亦保留 node_modules 原样（含 prebuild），不走 asar
cp -a node_modules/. "$DEST/node_modules/"

echo "== [4/4] 自检：spawn dsh web 应给出 URL（pilot-harness apps/desktop/src/main.ts 60s 超时同款，失败打印尾部）"
SMOKE_HOME="$(mktemp -d)"
# dsh web 常驻：timeout 到点返回 124/143，只看日志是否出现 URL（与 pilot 抽 URL 逻辑 extractHarnessServerUrl 一致）
timeout 60 env DSH_HOME="$SMOKE_HOME" DEEPSEEK_API_KEY=placeholder \
     "$DEST/node" "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" --profile web --port 0 \
     >"$TMP/smoke.log" 2>&1 || true
if grep -q "dsh web:" "$TMP/smoke.log"; then
  echo "   自检 OK：$(grep 'dsh web:' "$TMP/smoke.log" | head -1)"
else
  echo "error: 闭包自检失败——dsh 未给出 URL。尾部："
  tail -30 "$TMP/smoke.log" >&2
  rm -rf "$SMOKE_HOME"
  exit 1
fi
rm -rf "$SMOKE_HOME"

echo "== 完成 → $DEST"
"$DEST/node" -v
echo "dsh: $(grep '"version"' "$DEST/node_modules/@deepseek-ai/dsh/package.json" | head -1 | xargs)"
du -sh "$DEST" | cut -f1
echo "   入口校验：$DEST/node + $DEST/node_modules/@deepseek-ai/dsh/lib/bin.js"
[[ -f "$DEST/node" && -f "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" ]] || { echo "error: 入口缺失" >&2; exit 1; }
