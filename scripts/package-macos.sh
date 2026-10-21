#!/usr/bin/env bash
# package-macos.sh — 从 .NET publish 输出打 macOS 包（dmg，含 app bundle）。
# 参照 pilot-harness 的 mac 打包：此处为 .NET 自包含 publish 的等价物。
# online-first（ADR online-first-unbundled-runtime）：包只带壳 + 安装器自带插件资源
# （Contents/MacOS/resources/plugins/dsh-desktop-companion.tgz）；运行时 = 用户 PATH 全局 dsh。
# 布局：
#   DeepSeek.Harness.Desktop.app/Contents/MacOS/  = dotnet publish 全量
#   …/Contents/MacOS/resources/plugins/           = 插件 tgz
#   …/Contents/Resources/icon.icns                = 应用图标（sips 从 assets/icon.png 转）
#   …/Contents/Info.plist                         = bundle 元数据（CFBundleIconFile 指 icon.icns）
# 用法：
#   scripts/package-macos.sh [publish_dir]          # 全量（需 hdiutil + sips，产 dmg）
#   scripts/package-macos.sh --stage-only [dir]     # 仅组装 staging（需 sips）
#   scripts/package-macos.sh --self-test            # 离线夹具（共用头部 + 架构映射）
# 环境：VERSION、ARCH（x64/arm64）、SELF_SIGN=1（自签，仅内部/开发——实现见 scripts/dev-sign.sh）
set -euo pipefail

ROOT_SCRIPTS="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/packaging-common.sh
source "$ROOT_SCRIPTS/lib/packaging-common.sh"

if [[ "${1:-}" == "--self-test" ]]; then
  packaging_self_test
  exit $?
fi

packaging_init macos "$@"
APP_BUNDLE="DeepSeek.Harness.Desktop.app"
BUNDLE="$OUT/stage/$APP_BUNDLE"
CONTENT="$BUNDLE/Contents/MacOS"

echo "== 组装 staging: $OUT/stage/$APP_BUNDLE"
rm -rf "$OUT/stage" && mkdir -p "$CONTENT"
cp -r "$PUBLISH_DIR/." "$CONTENT/"
# 安装器自带插件资源：companion tgz 从仓库源码现打并校验（fail loud）。
# 资源一律 exe 目录相对（Contents/MacOS/resources/，与 Linux/Windows 同构）——
# 运行时侧（dsh 探测 / 插件解析）按 AppContext.BaseDirectory 探测；旧布局
# Resources/ 下的资源从未被探测到过（mac 无真机验证的潜伏布局 bug，本批顺势修正）。
# 不再捆绑运行时闭包——首启引导确保全局 dsh（ADR simple-shell-single-global-dsh）。
mkdir -p "$CONTENT/resources/plugins"
bash "$ROOT/scripts/build-companion-tgz.sh" "$CONTENT/resources/plugins/dsh-desktop-companion.tgz"
chmod +x "$CONTENT/DeepSeek.Harness.Desktop"

# 最小 Info.plist（签名占位，未做 codesign）
# 应用图标：macOS 只认 Contents/Resources 下的 .icns（publish 直出的 icon.png 落在
# Contents/MacOS/，Finder/Dock 不读它——包能启动但显示通用图标）。源为仓库 assets/icon.png。
mkdir -p "$BUNDLE/Contents/Resources"
command -v sips >/dev/null 2>&1 \
  || die "缺 sips（仅 macOS 可用），无法生成应用图标: $BUNDLE/Contents/Resources/icon.icns"
sips -s format icns "$ROOT/assets/icon.png" --out "$BUNDLE/Contents/Resources/icon.icns" >/dev/null \
  || die "图标转换失败: $ROOT/assets/icon.png → $BUNDLE/Contents/Resources/icon.icns"
cat > "$BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>DeepSeek Harness Desktop</string>
  <key>CFBundleIdentifier</key><string>io.github.ZK-Andy.dotnet-deepseek-harness-desktop</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>DeepSeek.Harness.Desktop</string>
  <key>CFBundleIconFile</key><string>icon.icns</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
</dict></plist>
EOF

# 布局断言（staging 即 dmg 的内容源）：闭包残留 / 插件资源 / 主程序 + 可执行位
packaging_assert_layout macos "$CONTENT"

# 自签（可选，仅内部/开发验证用；ad-hoc 或 MACOS_SIGN_IDENTITY 指定身份）。
# 显式 SELF_SIGN=1 才启用——不默认打扰现有发布（tag 触发的公开包仍保持未签名）。
# 签名必须在生成 dmg **之前**（dmg 内的 .app 无法再签）；实现唯一家在 scripts/dev-sign.sh。
if [[ "${SELF_SIGN:-0}" == "1" ]]; then
  bash "$ROOT_SCRIPTS/dev-sign.sh" macos "$BUNDLE"
fi

echo "== staging 体积: $(du -sh "$OUT/stage" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  find "$OUT/stage" -maxdepth 3 -type d | sort | head -20 || true
  ls -lh "$CONTENT/resources/plugins/" 2>&1 | head -3 || true
  exit 0
fi

mkdir -p "$OUT"
# 单一 dmg 产物（不再单独产出便携 zip——省去对闭包的重复压缩）。命名含 macos 标识。
DMG="$OUT/DeepSeek.Harness.Desktop_${VERSION}_macos-${ARCH}.dmg"
rm -f "$DMG"

if command -v hdiutil >/dev/null 2>&1; then
  echo "== 生成 dmg（hdiutil）: $DMG"
  # 单一调用，无 srcfolder 回退：回退针对的是同一个失败面（曾被 cfg 化/兼容化掩盖真因），
  # 失败即 fail loud（dmg 是唯一产物，无 zip 兜底）。
  hdiutil create -volname "DeepSeek Harness Desktop" -srcfolder "$BUNDLE" -ov -format UDZO "$DMG" 2>&1 | tail -20 \
    || { rm -f "$DMG"; die "dmg 生成失败（hdiutil create，无 zip 兜底）"; }
  echo "== 产物 dmg: $DMG ($(du -h "$DMG" | cut -f1))"
  hdiutil imageinfo "$DMG" 2>&1 | head -20 || true
elif command -v create-dmg >/dev/null 2>&1; then
  echo "== 生成 dmg（create-dmg）: $DMG"
  create-dmg --volname "DeepSeek Harness Desktop" --window-pos 200 120 --window-size 600 400 --icon-size 100 --app-drop-link 450 185 "$DMG" "$BUNDLE" 2>&1 | tail -20 \
    || die "dmg 生成失败（create-dmg，无 zip 兜底）"
  echo "== 产物 dmg: $DMG ($(du -h "$DMG" | cut -f1))"
else
  die "缺 hdiutil/create-dmg，无法产出 dmg（唯一产物）"
fi

echo "== 体积: $(du -sh "$OUT/stage" | cut -f1) → $(du -h "$DMG" | cut -f1) (dmg)"
