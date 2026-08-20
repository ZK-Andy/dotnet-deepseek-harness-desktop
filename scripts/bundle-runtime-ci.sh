#!/usr/bin/env bash
# bundle-runtime-ci.sh — 在 CI（或任意干净机）生成 resources/runtime（pilot-harness 同款整树方案）。
# 参照 pilot-harness：声明式依赖 @deepseek-ai/dsh 的完整闭包随产物收入，入口
# node_modules/@deepseek-ai/dsh/lib/bin.js；asar=false 思想下保留原样不打包为单文件。
# 产物：resources/runtime/{node, node_modules/...}（gitignore，发布时由 package-linux.sh 组装）
#   node: 来自 nodejs.org 的平台二进制
#   node_modules: pnpm 安装的完整依赖树（含跨平台 prebuild，原样保留 symlink 结构，入口同 pilot-harness apps/desktop/src/main.ts）
# 用法：bash scripts/bundle-runtime-ci.sh [linux-x64|linux-arm64|win-x64|osx-x64|osx-arm64]
set -euo pipefail

PLATFORM="${1:-linux-x64}"
NODE_VERSION="${NODE_VERSION:-22.23.1}"
DSH_VERSION="${DSH_VERSION:-0.1.0-rc.7}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/resources/runtime"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

case "$PLATFORM" in
  linux-x64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.xz"; NODE_BIN="node-v${NODE_VERSION}-linux-x64/bin/node"; NODE_DST="node" ;;
  linux-arm64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-arm64.tar.xz"; NODE_BIN="node-v${NODE_VERSION}-linux-arm64/bin/node"; NODE_DST="node" ;;
  win-x64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-win-x64.zip"; NODE_BIN="node-v${NODE_VERSION}-win-x64/node.exe"; NODE_DST="node.exe" ;;
  osx-x64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-darwin-x64.tar.gz"; NODE_BIN="node-v${NODE_VERSION}-darwin-x64/bin/node"; NODE_DST="node" ;;
  osx-arm64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-darwin-arm64.tar.gz"; NODE_BIN="node-v${NODE_VERSION}-darwin-arm64/bin/node"; NODE_DST="node" ;;
  *) echo "error: 暂不支持平台 $PLATFORM" >&2; exit 1 ;;
esac

echo "== [1/3] 下载 Node v${NODE_VERSION} ($PLATFORM)"
if [[ "$NODE_URL" == *.zip ]]; then
  curl -fsSL "$NODE_URL" -o "$TMP/node.zip"
  if command -v unzip >/dev/null 2>&1; then
    unzip -q "$TMP/node.zip" -d "$TMP"
  else
    powershell -Command "Expand-Archive -Path '$TMP/node.zip' -DestinationPath '$TMP' -Force" 2>/dev/null || unzip -q "$TMP/node.zip" -d "$TMP"
  fi
else
  curl -fsSL "$NODE_URL" -o "$TMP/node.tar.xz"
  if [[ "$NODE_URL" == *.tar.gz ]]; then tar -xzf "$TMP/node.tar.xz" -C "$TMP"; else tar -xJf "$TMP/node.tar.xz" -C "$TMP"; fi
fi
mkdir -p "$DEST"
cp "$TMP/$NODE_BIN" "$DEST/$NODE_DST"
if [[ "$NODE_DST" == "node" ]]; then chmod +x "$DEST/node"; fi
# Windows 下 pnpm 的 symlink/junction 在 Git Bash 的 cp -a 下失败，需解引用
CP_A="cp -a"
if [[ "$PLATFORM" == win-* ]]; then
  CP_A="cp -Lr"
fi

echo "== [2/3] pnpm 安装 @deepseek-ai/dsh@${DSH_VERSION} + dshmarket 依赖闭包（dshmarket 随包预装，首启后台 file:// 安装，不阻塞 dsh web:）"
if ! command -v pnpm >/dev/null 2>&1; then
  echo "    未发现 pnpm，npm install -g pnpm@11"
  npm install -g pnpm@11
fi
pnpm --version
mkdir -p "$TMP/app" "$TMP/store"
cd "$TMP/app"
npm init -y >/dev/null 2>&1
# 允许原生绑定构建脚本，否则 pnpm 11 将拒绝执行且闭包缺 .node 二进制
# 使用临时 store 避免默认 ~/.local/share/pnpm/store 的 sqlite 锁/RO 残留
pnpm add "@deepseek-ai/dsh@${DSH_VERSION}" --prod --store-dir "$TMP/store" \
  --allow-build=node-pty --allow-build=koffi --allow-build=protobufjs \
  --allow-build=@google/genai --allow-build=@deepseek-ai/dsh-subprocess-local
# 预装市场：与 dsh 同闭包，随包收入；另取官方已构建 tgz 供首启后后台 file:// 安装到 DSH_HOME（不阻塞 dsh web:）
# 旧版 pnpm pack dshmarket 会误打 app 包（394B），已改为直接拉 registry 官方 tgz（已含 lib/client，无需 tsc 构建）
pnpm add "dshmarket@1.15.0" --prod --store-dir "$TMP/store" --allow-build=esbuild
echo "   拉取 dshmarket 官方 tgz（跳过本地 pack 的 tsc/prepare 坑）"
if curl -fsSL "https://registry.npmjs.org/dshmarket/-/dshmarket-1.15.0.tgz" -o "$DEST/dshmarket.tgz" 2>/dev/null; then
  echo "   dshmarket tgz 已随包：$DEST/dshmarket.tgz ($(du -h "$DEST/dshmarket.tgz" | cut -f1))"
  # 轻量校验：包内应为 dshmarket 而非 app（直接校验 package.json 的 name）
  if ! tar -xOzf "$DEST/dshmarket.tgz" package/package.json 2>/dev/null | grep -q '"name": "dshmarket"'; then
    echo "warn: tgz 非 dshmarket 包，删除后回退" >&2
    rm -f "$DEST/dshmarket.tgz"
  fi
fi
# 回退：若 curl 失败（离线 CI），用已装的本地目录 tar 出正确包（package/ 前缀，跳过 lifecycle）
if [[ ! -s "$DEST/dshmarket.tgz" ]]; then
  echo "   官方 tgz 拉取失败，改由本地 node_modules/dshmarket 目录 tar（免构建）"
  REAL_DIR="$(realpath "$TMP/app/node_modules/dshmarket" 2>/dev/null || echo "")"
  if [[ -z "$REAL_DIR" ]]; then
    REAL_DIR="$(find "$TMP/app/node_modules/.pnpm" -type d -path "*dshmarket@1.15.0*/node_modules/dshmarket" -print -quit 2>/dev/null || echo "")"
  fi
  if [[ -n "$REAL_DIR" && -d "$REAL_DIR" ]]; then
    # 官方 tgz 仅含 package.json/cordis.patch.yml/lib/client/README/LICENSE 等，不含 node_modules
    # 直接用 tar 打 package/ 前缀，避免触发 npm 的 prepack/tsc
    rm -f "$DEST/dshmarket.tgz"
    (cd "$REAL_DIR" && tar -czf "$DEST/dshmarket.tgz" --transform 's,^\./,package/,' --transform 's,^\.,package,' package.json cordis.patch.yml lib client README.md README.zh.md LICENSE 2>/dev/null) || \
    (cd "$REAL_DIR" && tar -czf "$DEST/dshmarket.tgz" --transform 's,^,package/,' package.json cordis.patch.yml lib client 2>/dev/null) || true
    # 校验
    if ! tar -xOzf "$DEST/dshmarket.tgz" package/package.json 2>/dev/null | grep -q '"name": "dshmarket"'; then
      echo "warn: 本地 tar 仍异常，删除" >&2
      rm -f "$DEST/dshmarket.tgz"
    else
      echo "   本地 tar 已随包：$DEST/dshmarket.tgz ($(du -h "$DEST/dshmarket.tgz" | cut -f1))"
    fi
  else
    echo "warn: 未找到本地 dshmarket 目录，tgz 缺失，后续首启将走 registry 直装" >&2
    rm -f "$DEST/dshmarket.tgz"
  fi
fi
# 最终校验：错包特征 394B / app 名称
if [[ -f "$DEST/dshmarket.tgz" ]]; then
  SZ=$(stat -c%s "$DEST/dshmarket.tgz" 2>/dev/null || stat -f%z "$DEST/dshmarket.tgz" 2>/dev/null || echo 0)
  if [[ "$SZ" -lt 10240 ]]; then
    echo "error: dshmarket.tgz 过小（${SZ}B），疑似仍为 app 壳包" >&2
    tar -tzf "$DEST/dshmarket.tgz" 2>&1 | head -20 >&2
    exit 1
  fi
fi

echo "== [3/3] 组装 resources/runtime（整棵 node_modules，pilot-harness 同款）"
rm -rf "$DEST/dsh" "$DEST/node_modules"
mkdir -p "$DEST/node_modules"
# 保留 pnpm 内部相对 symlink 结构整树拷入；pilot-harness 亦保留 node_modules 原样（含 prebuild），不走 asar
if ! $CP_A node_modules/. "$DEST/node_modules/" 2>/dev/null; then
  echo "   cp $CP_A 失败，尝试解引用/robocopy 回退"
  if command -v powershell >/dev/null 2>&1; then
    powershell -Command "Copy-Item -Path 'node_modules/*' -Destination '$DEST/node_modules' -Recurse -Force" 2>/dev/null || true
  fi
  # 最后尝试普通 cp（解引用）
  cp -Lr node_modules/. "$DEST/node_modules/" 2>/dev/null || cp -r node_modules/. "$DEST/node_modules/" 2>/dev/null || true
  [[ -f "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" ]] || { echo "error: 拷贝后仍缺入口" >&2; exit 1; }
fi

# 体积裁剪（可选，TRIM=1 时启用）：移除文档/源码/测试，保留运行时必需
if [[ "${TRIM:-0}" == "1" ]]; then
  echo "   裁剪：移除 *.md/*.ts/*.map 等非运行时文件"
  find "$DEST/node_modules" -type f \( -name "*.md" ! -name "LICENSE*" \) -delete 2>/dev/null || true
  find "$DEST/node_modules" -type f -name "*.ts" ! -path "*lib/*" -delete 2>/dev/null || true
  find "$DEST/node_modules" -type f -name "*.map" -delete 2>/dev/null || true
  find "$DEST/node_modules" -type d -name "__tests__" -prune -exec rm -rf {} + 2>/dev/null || true
  echo "   裁剪后：$(du -sh "$DEST" | cut -f1)（原 ~421M）"
fi

echo "== [4/4] 自检：spawn dsh web 应给出 URL（Linux 强校验，mac/win 轻校验）"
if [[ "$PLATFORM" == linux-* ]]; then
  SMOKE_HOME="$(mktemp -d)"
  # dsh web 常驻：timeout 到点返回 124/143，只看日志是否出现 URL（与 pilot 抽 URL 逻辑 extractHarnessServerUrl 一致）
  if command -v timeout >/dev/null 2>&1; then
    TIMEOUT_CMD="timeout 60"
  elif command -v gtimeout >/dev/null 2>&1; then
    TIMEOUT_CMD="gtimeout 60"
  else
    TIMEOUT_CMD=""
  fi
  if [[ -n "$TIMEOUT_CMD" ]]; then
    $TIMEOUT_CMD env DSH_HOME="$SMOKE_HOME" DEEPSEEK_API_KEY=placeholder \
         "$DEST/$NODE_DST" "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" --profile web --port 0 \
         >"$TMP/smoke.log" 2>&1 || true
  else
    # 无 timeout 时直接后台跑 5s 后 kill
    env DSH_HOME="$SMOKE_HOME" DEEPSEEK_API_KEY=placeholder \
         "$DEST/$NODE_DST" "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" --profile web --port 0 \
         >"$TMP/smoke.log" 2>&1 & SMOKE_PID=$!; sleep 5; kill $SMOKE_PID 2>/dev/null || true; wait $SMOKE_PID 2>/dev/null || true
  fi
  if grep -q "dsh web:" "$TMP/smoke.log"; then
    echo "   自检 OK：$(grep 'dsh web:' "$TMP/smoke.log" | head -1)"
  else
    echo "error: 闭包自检失败——dsh 未给出 URL。尾部："
    tail -30 "$TMP/smoke.log" >&2
    rm -rf "$SMOKE_HOME"
    exit 1
  fi
  rm -rf "$SMOKE_HOME"
else
  # mac/win 轻校验：仅检查入口与 node 可执行，不做 60s 常驻
  echo "   轻校验($PLATFORM)：检查入口"
  ls -lh "$DEST/$NODE_DST" 2>&1 | head -1
  [[ -f "$DEST/$NODE_DST" && -f "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" ]] || { echo "error: 入口缺失" >&2; exit 1; }
  echo "   轻校验 OK"
fi

echo "== 完成 → $DEST"
if [[ -f "$DEST/$NODE_DST" ]]; then
  "$DEST/$NODE_DST" -v 2>&1 | head -1 || true
fi
echo "dsh: $(grep '"version"' "$DEST/node_modules/@deepseek-ai/dsh/package.json" | head -1 | xargs)"
du -sh "$DEST" | cut -f1
echo "   入口校验：$DEST/$NODE_DST + $DEST/node_modules/@deepseek-ai/dsh/lib/bin.js"
[[ -f "$DEST/$NODE_DST" && -f "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" ]] || { echo "error: 入口缺失" >&2; exit 1; }
