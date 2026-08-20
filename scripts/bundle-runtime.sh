#!/usr/bin/env bash
# bundle-runtime.sh — 本地开发用：产出与 CI 一致的 resources/runtime（pilot-harness 同款整树方案）。
#   布局：resources/runtime/{node, node_modules/@deepseek-ai/dsh/lib/bin.js}
#   入口与 RuntimeLocator.TryLocateBundled 一致：node + node_modules/@deepseek-ai/dsh/lib/bin.js
# 与 scripts/bundle-runtime-ci.sh 输出一致；CI 仍走后者（下载 Node + pnpm 闭包，免依赖本机环境）。
# 用法：
#   scripts/bundle-runtime.sh                        # 优先本机 NODE_SRC/DSH_SRC 拷闭包，否则提示用 bundle-runtime-ci.sh
#   NODE_SRC=/path/to/node DSH_SRC=/path/to/dsh scripts/bundle-runtime.sh
#   scripts/bundle-runtime.sh --from-ci              # 直接委托 bundle-runtime-ci.sh linux-x64
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/resources/runtime"

if [[ "${1:-}" == "--from-ci" ]]; then
  exec bash "$ROOT/scripts/bundle-runtime-ci.sh" linux-x64
fi

NODE_SRC="${NODE_SRC:-$HOME/.hermes/node/bin/node}"
DSH_SRC="${DSH_SRC:-$HOME/.local/lib/node_modules/@deepseek-ai/dsh}"

if [[ ! -x "$NODE_SRC" || ! -f "$DSH_SRC/lib/bin.js" ]]; then
  echo "本地 DSH 运行时源不完整："
  echo "  NODE_SRC=$NODE_SRC ($([[ -x "$NODE_SRC" ]] && echo OK || echo MISSING))"
  echo "  DSH_SRC =$DSH_SRC ($([[ -f "$DSH_SRC/lib/bin.js" ]] && echo OK || echo MISSING))"
  echo ""
  echo "建议直接用 CI 同款路径（下载 Node + pnpm 收集闭包，与 GitHub 产物一致）："
  echo "  bash scripts/bundle-runtime-ci.sh linux-x64"
  echo "或：bash scripts/bundle-runtime.sh --from-ci"
  exit 1
fi

echo "== 本地拷闭包 → $DEST（整棵 node_modules，pilot-harness 同款）"
mkdir -p "$DEST"

echo "   node: $NODE_SRC → $DEST/node"
cp "$NODE_SRC" "$DEST/node"
chmod +x "$DEST/node"

# 本机 DSH 安装若为 pnpm 全局，依赖闭包在其父 node_modules；若为 npm 全局则与 DSH_SRC 同级。
# 统一收集为 DEST/node_modules 整树，保留 symlink 结构（与 bundle-runtime-ci.sh cp -a 一致）。
NODE_MODULES_SRC="$(cd "$(dirname "$DSH_SRC")/.." && pwd)"
if [[ -d "$NODE_MODULES_SRC/.pnpm" || -d "$NODE_MODULES_SRC/@deepseek-ai" ]]; then
  echo "   node_modules: $NODE_MODULES_SRC → $DEST/node_modules（cp -a 保留 symlink）"
  rm -rf "$DEST/node_modules" "$DEST/dsh"
  mkdir -p "$DEST/node_modules"
  cp -a "$NODE_MODULES_SRC"/. "$DEST/node_modules/"
else
  # 退化：仅拷 DSH 包本体（闭包不全，启动会失败；仅作调试提示）
  echo "warn: 未找到 $NODE_MODULES_SRC 的 pnpm 结构，仅拷 DSH 包本体（闭包不全）" >&2
  rm -rf "$DEST/node_modules" "$DEST/dsh"
  mkdir -p "$DEST/node_modules/@deepseek-ai"
  cp -r "$DSH_SRC" "$DEST/node_modules/@deepseek-ai/dsh"
fi

echo "== 自检：RuntimeLocator 探测"
if [[ -f "$DEST/node" && -f "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" ]]; then
  echo "   OK: $DEST/node + $DEST/node_modules/@deepseek-ai/dsh/lib/bin.js"
else
  echo "error: 产出不符合 RuntimeLocator 预期" >&2
  ls -R "$DEST" | head -30
  exit 1
fi

echo "== 完成。体积：$(du -sh "$DEST" | cut -f1)；dsh：$(grep '"version"' "$DEST/node_modules/@deepseek-ai/dsh/package.json" | head -1)"
echo "   覆盖测试：DSH_DESKTOP_RUNTIME_DIR=$DEST"
