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
NODE_VERSION="${NODE_VERSION:-24.20.0}"
DSH_VERSION="${DSH_VERSION:-0.1.1-rc.2}"
# 随包插件市场版本（market 正典单点）：闭包签名含此维度——market 版本变化而
# dsh/node/companion 未变时，旧闭包缓存必须失效（v0.3.8 教训：meta 签名缺此
# 维度，restore-key 回退捡回旧闭包，版本感知升级拿不到新 market）。
MARKET_VERSION="${MARKET_VERSION:-1.36.0}"
# 裁剪策略版本（trimPolicy 正典单点）：trim_runtime_closure 行为变更时必须 bump——
# meta 签名含此维度，restore-key 前缀命中捡回的旧闭包因 trimPolicy 不匹配而强制全量重建
# （v0.3.8 同类洞的同步根治：cache key 的 hashFiles 只保证精确 key miss，前缀命中仍可绕过；
# 维度入库后校验必拦。照 MARKET_VERSION 先例：显式常量、入库、校验对照）。
# 另有 meta scriptSha256 维度（本脚本自身 sha）：脚本任何改动即旧闭包失效，机器兜底，
# 本注释要求的 bump 漏做也不再放过脚本变更。
TRIM_POLICY="${TRIM_POLICY:-strip-sourcemap-2026-08}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/resources/runtime"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# pnpm store：跨 run 持久化缓存的关键路径。CI 的 actions/cache 按此缓存，
# 命中后不必每次重下包/重编原生模块（node-pty/koffi/protobufjs…）。默认统一放
# $HOME/.dsh-pnpm/store（各平台一致，Git Bash 下 Windows 亦正确解析），可用
# PNPM_STORE_DIR 覆盖（本地 /home 只读等场景由 bundle-runtime.sh 指向工作区可写处）。
PNPM_STORE_DIR="${PNPM_STORE_DIR:-$HOME/.dsh-pnpm/store}"
mkdir -p "$PNPM_STORE_DIR"

case "$PLATFORM" in
  linux-x64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.xz"; NODE_BIN="node-v${NODE_VERSION}-linux-x64/bin/node"; NODE_DST="node" ;;
  linux-arm64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-arm64.tar.xz"; NODE_BIN="node-v${NODE_VERSION}-linux-arm64/bin/node"; NODE_DST="node" ;;
  win-x64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-win-x64.zip"; NODE_BIN="node-v${NODE_VERSION}-win-x64/node.exe"; NODE_DST="node.exe" ;;
  osx-x64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-darwin-x64.tar.gz"; NODE_BIN="node-v${NODE_VERSION}-darwin-x64/bin/node"; NODE_DST="node" ;;
  osx-arm64) NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-darwin-arm64.tar.gz"; NODE_BIN="node-v${NODE_VERSION}-darwin-arm64/bin/node"; NODE_DST="node" ;;
  *) echo "error: 暂不支持平台 $PLATFORM" >&2; exit 1 ;;
esac

# 组装好的闭包缓存命中：resources/runtime 由 CI 的 actions/cache 整步恢复（含 .bundle-meta.json），
# 且签名与本次请求一致 → 整步跳过（免下载 Node/免 pnpm/免 cp/免自检）。本地/冷启动无缓存则正常全量。
# 签名含随包插件源码哈希：插件内容变更而 dsh/node 未变时，旧缓存必须失效（v0.2.0 曾因此带出 0.0.1 旧 tgz）。
companion_sha() {
  find "$ROOT/plugins/dsh-desktop-companion" -type f -print0 2>/dev/null | sort -z \
    | xargs -0 sha256sum 2>/dev/null | sha256sum | cut -d' ' -f1
}
COMPANION_SHA="$(companion_sha)"
# 脚本自身 sha 入库（trimSha256 承诺的落地）：脚本任何改动（含裁剪/组装逻辑）都会让
# restore-key 前缀命中捡回的旧闭包校验失败 → 必全量重建——不再依赖手工 bump TRIM_POLICY。
SCRIPT_SHA="$(sha256sum "${BASH_SOURCE[0]}" | cut -d' ' -f1)"
META_FILE="$DEST/.bundle-meta.json"
if [[ -f "$META_FILE" ]] \
   && grep -q "\"dshVersion\":\"$DSH_VERSION\"" "$META_FILE" \
   && grep -q "\"nodeVersion\":\"$NODE_VERSION\"" "$META_FILE" \
   && grep -q "\"platform\":\"$PLATFORM\"" "$META_FILE" \
   && grep -q "\"companionSha256\":\"$COMPANION_SHA\"" "$META_FILE" \
   && grep -q "\"market\":\"$MARKET_VERSION\"" "$META_FILE" \
   && grep -q "\"trimPolicy\":\"$TRIM_POLICY\"" "$META_FILE" \
   && grep -q "\"scriptSha256\":\"$SCRIPT_SHA\"" "$META_FILE" \
   && [[ -f "$DEST/$NODE_DST" ]] \
   && [[ -f "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" ]]; then
  echo "== resources/runtime 闭包缓存命中（$PLATFORM，dsh $DSH_VERSION，companion $COMPANION_SHA）→ 跳过重建 =="
  du -sh "$DEST" | cut -f1
  exit 0
fi
if [[ -f "$META_FILE" ]]; then
  echo "  闭包存在但签名不匹配（$PLATFORM / dsh $DSH_VERSION / node $NODE_VERSION / companion $COMPANION_SHA / market $MARKET_VERSION），全量重建"
fi

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
# pnpm 钉 11.7.0：本地安装 + .bin 直调（PATH 无关、跨三平台一致，不依赖 runner 预装/全局 npm；
# npm -g 安装会被旧 PATH 遮蔽——v0.1.20 Windows 实证）。理由：runner 预装 pnpm 随镜像漂移
# （11.22.0 三平台构建失败——按严格 node-semver 预发布规则，dshmarket 的 optional peer
# dsh-settings@^0.1.0-rc.7 匹配不到已发布的 0.1.1-rc.1 而 ERR_PNPM_NO_MATCHING_VERSION；11.7.0
# 本地闭包自检 OK）。安装失败由 set -euo pipefail fail loud。
PNPM_DIST="$ROOT/.cache/pnpm-11.7.0"
PNPM_BIN="$PNPM_DIST/node_modules/.bin/pnpm"
if [[ ! -x "$PNPM_BIN" ]]; then
  echo "    安装本地 pnpm 11.7.0 → $PNPM_DIST/node_modules"
  mkdir -p "$PNPM_DIST"
  npm_config_cache="$ROOT/.cache/npm" npm install --prefix "$PNPM_DIST" pnpm@11.7.0 --no-save
fi
echo "    pnpm: $("$PNPM_BIN" --version)"
mkdir -p "$TMP/app"
cd "$TMP/app"
npm init -y >/dev/null 2>&1
# 允许原生绑定构建脚本，否则 pnpm 11 将拒绝执行且闭包缺 .node 二进制
# store 指向 PNPM_STORE_DIR（CI 持久化缓存命中后免重复下包/编译原生模块）
"$PNPM_BIN" add "@deepseek-ai/dsh@${DSH_VERSION}" --prod --store-dir "$PNPM_STORE_DIR" \
  --allow-build=node-pty --allow-build=koffi --allow-build=protobufjs \
  --allow-build=@google/genai --allow-build=@deepseek-ai/dsh-subprocess-local
# 预装市场：与 dsh 同闭包，随包收入；另取官方已构建 tgz 供首启后后台 file:// 安装到 DSH_HOME（不阻塞 dsh web:）
# 旧版 pnpm pack dshmarket 会误打 app 包（394B），已改为直接拉 registry 官方 tgz（已含 lib/client，无需 tsc 构建）
"$PNPM_BIN" add "dshmarket@${MARKET_VERSION}" --prod --store-dir "$PNPM_STORE_DIR" --allow-build=esbuild
echo "   拉取 dshmarket 官方 tgz（跳过本地 pack 的 tsc/prepare 坑）"
if curl -fsSL "https://registry.npmjs.org/dshmarket/-/dshmarket-${MARKET_VERSION}.tgz" -o "$DEST/dshmarket.tgz" 2>/dev/null; then
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
    REAL_DIR="$(find "$TMP/app/node_modules/.pnpm" -type d -path "*dshmarket@${MARKET_VERSION}*/node_modules/dshmarket" -print -quit 2>/dev/null || echo "")"
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

# 桌面伴生插件（dsh-desktop-companion：外部链接接管等壳集成）：仓库源码直接 tar 随包。
# staging 目录法打出 package/ 前缀——macOS bsdtar 无 GNU tar 的 --transform，staging 三平台一致。
COMPANION_SRC="$ROOT/plugins/dsh-desktop-companion"
if [[ ! -f "$COMPANION_SRC/package.json" ]]; then
  echo "error: 未找到 $COMPANION_SRC/package.json（桌面伴生插件源码缺失）" >&2
  exit 1
fi
rm -rf "$TMP/companion-pkg"
mkdir -p "$TMP/companion-pkg/package"
cp "$COMPANION_SRC/package.json" "$COMPANION_SRC/cordis.patch.yml" "$TMP/companion-pkg/package/"
cp -r "$COMPANION_SRC/lib" "$COMPANION_SRC/client" "$TMP/companion-pkg/package/"
(cd "$TMP/companion-pkg" && tar -czf "$DEST/dsh-desktop-companion.tgz" package)
if ! tar -xOzf "$DEST/dsh-desktop-companion.tgz" package/package.json 2>/dev/null | grep -q '"name": "dsh-desktop-companion"'; then
  echo "error: dsh-desktop-companion.tgz 打包校验失败" >&2
  exit 1
fi
echo "   dsh-desktop-companion tgz 已随包：$DEST/dsh-desktop-companion.tgz ($(du -h "$DEST/dsh-desktop-companion.tgz" | cut -f1))"

echo "== [3/3] 组装 resources/runtime（整棵 node_modules，pilot-harness 同款）"
rm -rf "$DEST/dsh" "$DEST/node_modules"
mkdir -p "$DEST/node_modules"
# 拷贝语义必须保住 pnpm 链接结构：Node 从「包的真实位置」向上解析依赖，
# 顶层入口若被解引用成孤立真目录（cp -Lr 对 junction 的实际行为），其
# .pnpm 兄弟上下文即断裂——Windows 首跑图校验实证全图缺件。
#  - Linux/mac: cp -a 保留 symlink
#  - Windows: robocopy /SL 保留 junction/symlink 本身（与 cp -a 语义对称）；
#    robocopy 退出码 <8 均为成功（1=拷了文件 2=有 extra 等按位提示）
if [[ "$PLATFORM" == win-* ]] && command -v robocopy >/dev/null 2>&1; then
  SRC_WIN="$(cygpath -w "$(pwd)/node_modules")"
  DST_WIN="$(cygpath -w "$DEST/node_modules")"
  echo "   robocopy /SL 保留链接结构: $SRC_WIN → $DST_WIN"
  # MSYS2_ARG_CONV_EXCL：Git Bash 会把 /E 这类开关当 POSIX 路径转成 "E:/"（首跑实证
  # Invalid Parameter #3），排除转换后原样传给 robocopy
  MSYS2_ARG_CONV_EXCL='*' robocopy "$SRC_WIN" "$DST_WIN" /E /SL /NFL /NDL /NJH /NJS /NP || RC=$?
  RC=${RC:-0}
  if [[ $RC -ge 8 ]]; then
    echo "warn: robocopy 失败 rc=$RC，回退 cp 解引用（图校验将把关链接完整性）" >&2
    $CP_A node_modules/. "$DEST/node_modules/" || true
  fi
else
  # 保留 pnpm 内部相对 symlink 结构整树拷入；pilot-harness 亦保留 node_modules 原样（含 prebuild），不走 asar
  $CP_A node_modules/. "$DEST/node_modules/"
fi
[[ -f "$DEST/node_modules/@deepseek-ai/dsh/lib/bin.js" ]] || { echo "error: 拷贝后仍缺入口" >&2; exit 1; }

# 裁剪闭包（per-arch + 无风险冗余）：
#  - node-pty 删「非当前平台」prebuild 目录（node-pty 运行时按 process.platform+arch 选目录，删别的平台安全）
#  - 删 *.map 源码映射（仅调试用）与 README/CHANGELOG/CONTRIBUTING/HISTORY markdown
#  - 不删 .ts/.d.ts 源码（避免历次盲删 TRIM 的运行时故障风险）
# 作用于 $DEST/node_modules；须在自检前执行，让自检验证裁剪后闭包仍能启动。
trim_runtime_closure() {
  local keep=""
  case "$PLATFORM" in
    linux-x64) keep="linux-x64" ;;
    linux-arm64) keep="linux-arm64" ;;
    win-x64) keep="win32-x64" ;;
    osx-x64) keep="darwin-x64" ;;
    osx-arm64) keep="darwin-arm64" ;;
  esac
  echo "== 裁剪闭包：node-pty 保留 $keep；删 *.map / README·CHANGELOG markdown + 剥 sourceMappingURL 注释 =="
  find "$DEST/node_modules/.pnpm" -path '*/node-pty*/node_modules/node-pty/prebuilds/*' -type d 2>/dev/null | while read -r d; do
    case "$(basename "$d")" in
      "$keep") : ;;
      *) rm -rf "$d" ;;
    esac
  done
  find "$DEST/node_modules" -name '*.map' -type f -delete 2>/dev/null || true
  # 剥 sourceMappingURL 尾注释（ADR strip-sourcemap-comments-in-closure）：上游构建产物普遍带
  # `//# sourceMappingURL=xxx.js.map` 尾注释，但 .map 已在上行删掉——注释还留着就会让 DevTools
  # 按不存在的映射去抓而刷 404（实测：index/vendor/dsh-client-* 的 .map 各档案 404 噪声）。
  # 只删行首锚定的注释行，运行时零语义影响；缓存失效由 meta 的 scriptSha256 维度保证
  # （本脚本自身 sha 入库并参与命中校验，脚本改动即旧闭包校验失败 → 必全量重建，restore-key
  # 前缀命中无法绕过——v0.3.8「restore-key 回退捡旧闭包」教训的同类洞的根治）。
  # perl -i 比 GNU/BSD sed 语法统一（macos runner 是 BSD sed），临时文件列表喂 xargs 且
  # -n 64 显式分批：win Git Bash 命令行上限 32K（实测单条路径 ~150B+前缀，200 一批会超），
  # 同时规避 grep 零命中时 xargs 仍空跑一次 perl 打 stderr warning（上守卫挡空列表）。
  local mlist="$TMP/swmap-list.$$"
  grep -rlZ --include='*.js' --include='*.mjs' --include='*.cjs' '^//# sourceMappingURL=' "$DEST/node_modules" 2>/dev/null > "$mlist" || true
  if [[ -s "$mlist" ]]; then
    # perl 一步故意 fail loud（不挂 || true）：剥注释是交付物自洽要求，失败必须显眼，
    # 不能像兄弟行那样静默跳过——否则发版闭包仍带悬空注释，405 噪声复发且无迹可查。
    xargs -0 -n 64 perl -i -ne 'print unless m{^//# sourceMappingURL=}' < "$mlist"
  fi
  rm -f "$mlist"
  find "$DEST/node_modules" -type f \( -iname 'README*.md' -o -iname 'CHANGELOG*.md' -o -iname 'CONTRIBUTING*.md' -o -iname 'HISTORY*.md' \) -delete 2>/dev/null || true
}

trim_runtime_closure

# 一方闭包图静态校验（批次一，ADR artifact-verification-chain）：裁剪后、自检前——
# @deepseek-ai/* 生产依赖图全供给才放行，缺件在构建时 fail loud 而非运行时。
echo "== [3.5/4] 一方闭包图静态校验"
"$DEST/$NODE_DST" "$ROOT/scripts/verify-closure-graph.mjs" "$DEST"

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
# 成功构建后写闭包签名，供下次（CI 缓存恢复后）整步跳过
printf '{"dshVersion":"%s","nodeVersion":"%s","platform":"%s","companionSha256":"%s","market":"%s","trimPolicy":"%s","scriptSha256":"%s"}\n' \
  "$DSH_VERSION" "$NODE_VERSION" "$PLATFORM" "$(companion_sha)" "$MARKET_VERSION" "$TRIM_POLICY" "$SCRIPT_SHA" > "$DEST/.bundle-meta.json"
echo "   已写闭包元数据：$DEST/.bundle-meta.json"
