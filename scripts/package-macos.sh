#!/usr/bin/env bash
# package-macos.sh — 从 .NET publish 输出打 macOS 包（dmg，含 app bundle）。
# 参照 pilot-harness 的 mac 打包：此处为 .NET 自包含 publish + resources/runtime 手工组装。
# 布局：
#   DeepSeek.Harness.Desktop.app/Contents/MacOS/  = dotnet publish 全量
#   DeepSeek.Harness.Desktop.app/Contents/Resources/runtime/ = resources/runtime（含对应架构的 node）
# 用法：
#   scripts/package-macos.sh [publish_dir]          # 全量（需 hdiutil，产 dmg）
#   scripts/package-macos.sh --stage-only [dir]     # 仅组装 staging
# 环境：VERSION、ARCH（x64/arm64）、APP
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARG1="${1:-}"
STAGE_ONLY=0
if [[ "$ARG1" == "--stage-only" ]]; then STAGE_ONLY=1; ARG1="${2:-}"; fi

ARCH_RAW="${ARCH:-arm64}"
case "$ARCH_RAW" in
  x64|amd64|x86_64) ARCH="x64"; RID="osx-x64"; OUT_SUFFIX="osx-x64" ;;
  arm64|aarch64) ARCH="arm64"; RID="osx-arm64"; OUT_SUFFIX="osx-arm64" ;;
  *) echo "error: 不支持 ARCH=${ARCH_RAW}（仅 x64/arm64）" >&2; exit 1 ;;
esac

PUBLISH_DIR="${ARG1:-$ROOT/artifacts/publish-$RID}"
VERSION="${VERSION:-0.1.0}"
APP="DeepSeek Harness Desktop"
APP_BUNDLE="DeepSeek.Harness.Desktop.app"
OUT="$ROOT/artifacts/$OUT_SUFFIX"
STAGE="$OUT/stage"

[[ -d "$PUBLISH_DIR" ]] || { echo "error: publish 目录不存在: $PUBLISH_DIR" >&2; exit 1; }

echo "== 组装 staging: $STAGE/$APP_BUNDLE"
rm -rf "$STAGE" && mkdir -p "$STAGE/$APP_BUNDLE/Contents/MacOS" "$STAGE/$APP_BUNDLE/Contents/Resources"

cp -r "$PUBLISH_DIR/." "$STAGE/$APP_BUNDLE/Contents/MacOS/"
if [[ -d "$ROOT/resources/runtime" ]]; then
  echo "   并入 resources/runtime"
  cp -a "$ROOT/resources/runtime" "$STAGE/$APP_BUNDLE/Contents/Resources/"
  if [[ ! -f "$STAGE/$APP_BUNDLE/Contents/Resources/runtime/node" && ! -f "$STAGE/$APP_BUNDLE/Contents/Resources/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js" ]]; then
    echo "warn: staging 的 resources/runtime 可能架构不匹配（$RID）" >&2
    ls -R "$STAGE/$APP_BUNDLE/Contents/Resources/runtime" 2>&1 | head -20 >&2 || true
  fi
  # 校验 dshmarket
  if [[ -f "$STAGE/$APP_BUNDLE/Contents/Resources/runtime/dshmarket.tgz" ]]; then
    SZ=$(stat -c%s "$STAGE/$APP_BUNDLE/Contents/Resources/runtime/dshmarket.tgz" 2>/dev/null || stat -f%z "$STAGE/$APP_BUNDLE/Contents/Resources/runtime/dshmarket.tgz" 2>/dev/null || echo 0)
    if [[ "$SZ" -lt 10240 ]]; then
      echo "error: dshmarket.tgz 过小（${SZ}B）" >&2; exit 1
    fi
  fi
fi
chmod +x "$STAGE/$APP_BUNDLE/Contents/MacOS/DeepSeek.Harness.Desktop" 2>/dev/null || true

# 最小 Info.plist（签名占位，未做 codesign）
cat > "$STAGE/$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>DeepSeek Harness Desktop</string>
  <key>CFBundleIdentifier</key><string>io.github.ZK-Andy.dotnet-deepseek-harness-desktop</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>DeepSeek.Harness.Desktop</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
</dict></plist>
EOF

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  find "$STAGE" -maxdepth 3 -type d | sort | head -20 || true
  ls -lh "$STAGE/$APP_BUNDLE/Contents/Resources/runtime/node" 2>&1 | head -1 || echo "node 缺失"
  exit 0
fi

# 自签（可选，仅内部/开发验证用；ad-hoc 或 MACOS_SIGN_IDENTITY 指定身份）。
# 显式 SELF_SIGN=1 才启用——不默认打扰现有发布（tag 触发的公开包仍保持未签名）。
# 注意：自签/ad-hoc 不消除终端用户 Gatekeeper「来自身份不明的开发者」告警，仅治本机/内部。
sign_macos() {
  local identity="${MACOS_SIGN_IDENTITY:--}"
  if ! command -v codesign >/dev/null 2>&1; then
    echo "error: SELF_SIGN=1 但缺 codesign（仅 macOS 可用）" >&2; exit 1
  fi
  echo "== codesign 自签（identity=${identity}）: $APP_BUNDLE"
  codesign --force --deep --sign "$identity" "$STAGE/$APP_BUNDLE" || { echo "error: codesign 自签失败" >&2; exit 1; }
  codesign --verify --deep --strict "$STAGE/$APP_BUNDLE" || { echo "error: codesign 校验失败" >&2; exit 1; }
  echo "  ad-hoc/自签通过（对终端用户 Gatekeeper 无效，属内部/开发验证）"
}
if [[ "${SELF_SIGN:-0}" == "1" ]]; then sign_macos; fi

mkdir -p "$OUT"
# 单一 dmg 产物（不再单独产出便携 zip——省去对闭包的重复压缩）。命名含 macos 标识。
DMG="$OUT/DeepSeek.Harness.Desktop_${VERSION}_macos-${ARCH}.dmg"
rm -f "$DMG"

if command -v hdiutil >/dev/null 2>&1; then
  echo "== 生成 dmg（hdiutil）: $DMG"
  # 临时 dmg 卷名与窗口布局极简，签名占位
  if hdiutil create -volname "DeepSeek Harness Desktop" -srcfolder "$STAGE/$APP_BUNDLE" -ov -format UDZO "$DMG" 2>&1 | tail -20; then
    echo "== 产物 dmg: $DMG ($(du -h "$DMG" | cut -f1))"
    hdiutil imageinfo "$DMG" 2>&1 | head -20 || true
  else
    echo "warn: hdiutil create 失败，尝试回退为 srcfolder=$STAGE" >&2
    if hdiutil create -volname "DeepSeek Harness Desktop" -srcfolder "$STAGE" -ov -format UDZO "$DMG" 2>&1 | tail -20; then
      echo "== 产物 dmg(回退): $DMG ($(du -h "$DMG" | cut -f1))"
    else
      echo "error: dmg 生成失败（dmg 为唯一产物，无 zip 兜底）" >&2
      rm -f "$DMG"
      exit 1
    fi
  fi
elif command -v create-dmg >/dev/null 2>&1; then
  echo "== 生成 dmg（create-dmg）: $DMG"
  create-dmg --volname "DeepSeek Harness Desktop" --window-pos 200 120 --window-size 600 400 --icon-size 100 --app-drop-link 450 185 "$DMG" "$STAGE/$APP_BUNDLE" 2>&1 | tail -20 || { echo "error: create-dmg 失败（dmg 为唯一产物）" >&2; exit 1; }
  echo "== 产物 dmg: $DMG ($(du -h "$DMG" | cut -f1))"
else
  echo "error: 缺 hdiutil/create-dmg，无法产出 dmg（唯一产物）" >&2
  exit 1
fi

echo "== 体积: $(du -sh "$STAGE" | cut -f1) → $(du -h "$DMG" | cut -f1) (dmg)"
